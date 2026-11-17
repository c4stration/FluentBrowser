using Microsoft.Web.WebView2.Core;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace FluentBrowser.Shared;

internal sealed class FingerprintingProtectionService
{
    public const string SettingName = "FingerprintingProtection";

    private const string ProtectionScript = """
        (() => {
          'use strict';

          const removeMember = (proto, name) => {
            try {
              if (!proto) return;
              const descriptor = Object.getOwnPropertyDescriptor(proto, name);
              if (descriptor && descriptor.configurable)
                delete proto[name];
            } catch (_) { }
          };

          if (typeof Navigator !== 'undefined')
            removeMember(Navigator.prototype, 'getBattery');

          if (typeof Keyboard !== 'undefined')
            removeMember(Keyboard.prototype, 'getLayoutMap');
        })();
        """;

    private sealed class CoreState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public string? ScriptId { get; set; }

        public bool DocumentProtected { get; set; }
    }

    private readonly ConditionalWeakTable<CoreWebView2, CoreState> _states =
        new();

    private volatile bool _enabled;

    public FingerprintingProtectionService()
    {
        _enabled = GetEnabled();
    }

    public bool Enabled => _enabled;

    public static bool GetEnabled() =>
        ApplicationData.Current.LocalSettings.Values[SettingName]
            as bool? ?? false;

    public void SetEnabled(bool enabled) => _enabled = enabled;

    public Task AttachAsync(CoreWebView2 core)
    {
        ArgumentNullException.ThrowIfNull(core);

        bool created = false;

        CoreState state = _states.GetValue(core, _ =>
        {
            created = true;
            return new CoreState();
        });

        if (created)
        {
            core.PermissionRequested += OnPermissionRequested;

            core.ContentLoading += (_, _) =>
                state.DocumentProtected = state.ScriptId is not null;
        }

        return SynchronizeAsync(core, state);
    }

    public bool NeedsReload(CoreWebView2 core) =>
        _states.TryGetValue(core, out CoreState? state) &&
        (state.ScriptId is not null) != state.DocumentProtected;

    private async Task SynchronizeAsync(CoreWebView2 core, CoreState state)
    {
        await state.Gate.WaitAsync();

        try
        {
            while (true)
            {
                bool desired = _enabled;
                bool registered = state.ScriptId is not null;

                if (desired == registered)
                    return;

                if (desired)
                {
                    state.ScriptId =
                        await core.AddScriptToExecuteOnDocumentCreatedAsync(
                            ProtectionScript);
                }
                else
                {
                    core.RemoveScriptToExecuteOnDocumentCreated(
                        state.ScriptId!);

                    state.ScriptId = null;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to synchronize fingerprinting protection: {ex}");
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private void OnPermissionRequested(
        object? sender,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        if (!_enabled || !IsFingerprintingPermission(args.PermissionKind))
            return;

        args.State = CoreWebView2PermissionState.Deny;

        args.SavesInProfile = false;
    }

    private static bool IsFingerprintingPermission(
        CoreWebView2PermissionKind kind) =>
        kind is CoreWebView2PermissionKind.LocalFonts
            or CoreWebView2PermissionKind.WindowManagement
            or CoreWebView2PermissionKind.OtherSensors;
}
