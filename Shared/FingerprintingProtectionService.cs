using Microsoft.Web.WebView2.Core;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace FluentBrowser.Shared;

internal sealed class FingerprintingProtectionService
{
    public const string SettingName = "FingerprintingProtection";
    public const string CanvasProtectionSettingName = "CanvasProtection";
    public const string TimezoneSettingName = "TimezoneSpoofing";
    public const string DefaultTimezoneId = "UTC";

    private const string ProtectionScriptTemplate = """
        (() => {
          'use strict';

          const sessionSeed = '__SESSION_SEED__';
          const currentOrigin = (() => {
            if (location.origin && location.origin !== 'null')
              return location.origin;

            const random = new Uint32Array(2);
            try {
              crypto.getRandomValues(random);
            } catch (_) {
              random[0] = Math.floor(Math.random() * 0xffffffff);
              random[1] = Math.floor(Math.random() * 0xffffffff);
            }
            return `opaque:${random[0]}:${random[1]}`;
          })();
          const canvasProtectionEnabled = __CANVAS_PROTECTION__;
          const maxCanvasPixels = 4 * 1024 * 1024;

          function hashString64(value) {
            let hash = 0xcbf29ce484222325n;
            for (let i = 0; i < value.length; i++) {
              hash ^= BigInt(value.charCodeAt(i));
              hash = BigInt.asUintN(64, hash * 0x100000001b3n);
            }
            return hash;
          }

          function mix32(value) {
            value = (value ^ (value >>> 16)) >>> 0;
            value = Math.imul(value, 0x7feb352d) >>> 0;
            value = (value ^ (value >>> 15)) >>> 0;
            value = Math.imul(value, 0x846ca68b) >>> 0;
            return (value ^ (value >>> 16)) >>> 0;
          }

          function deriveToken(feature) {
            return hashString64(`${sessionSeed}|${currentOrigin}|${feature}`);
          }

          function foldToken(token) {
            const low = Number(token & 0xffffffffn);
            const high = Number((token >> 32n) & 0xffffffffn);
            return mix32(low ^ mix32(high));
          }

          const canvasToken = foldToken(deriveToken('canvas'));
          const audioToken = foldToken(deriveToken('audio'));
          const hardwareToken = foldToken(deriveToken('hardware'));

          function defineGetter(prototype, name, getter) {
            if (!prototype)
              return;

            try {
              const descriptor = Object.getOwnPropertyDescriptor(prototype, name);
              if (!descriptor || !descriptor.configurable)
                return;

              Object.defineProperty(prototype, name, {
                ...descriptor,
                get: getter
              });
            } catch (_) { }
          }

          function wrapMethod(prototype, name, createWrapper) {
            if (!prototype)
              return null;

            try {
              const descriptor = Object.getOwnPropertyDescriptor(prototype, name);
              if (!descriptor || typeof descriptor.value !== 'function' || !descriptor.configurable)
                return null;

              const original = descriptor.value;
              Object.defineProperty(prototype, name, {
                ...descriptor,
                value: createWrapper(original)
              });
              return original;
            } catch (_) {
              return null;
            }
          }

          function removeMember(prototype, name) {
            try {
              if (!prototype)
                return;

              const descriptor = Object.getOwnPropertyDescriptor(prototype, name);
              if (descriptor && descriptor.configurable)
                delete prototype[name];
            } catch (_) { }
          }

          function possibleCoreCounts(actual) {
            let count = Math.max(2, Math.min(16, Math.floor(actual) || 4));
            if (count % 2 !== 0)
              count--;

            const candidates = [count];
            if (count > 4)
              candidates.push(count - 4);
            if (count > 2)
              candidates.push(count - 2);
            return candidates;
          }

          if (typeof Navigator !== 'undefined') {
            const descriptor = Object.getOwnPropertyDescriptor(
              Navigator.prototype,
              'hardwareConcurrency'
            );

            if (descriptor?.get) {
              defineGetter(Navigator.prototype, 'hardwareConcurrency', function () {
                const actual = Number(descriptor.get.call(this)) || 4;
                const candidates = possibleCoreCounts(actual);
                const index = mix32(hardwareToken ^ Math.floor(actual)) % candidates.length;
                return candidates[index];
              });
            }

            removeMember(Navigator.prototype, 'getBattery');
          }

          if (typeof Keyboard !== 'undefined')
            removeMember(Keyboard.prototype, 'getLayoutMap');
          
          function getPixelIndex(x, y, width) {
            return (y * width + x) * 4;
          }

          function neighborhoodContentSeed(source, width, height, x, y, seed) {
            let result = seed;
            for (let dy = -1; dy <= 1; dy++) {
              const sampleY = Math.max(0, Math.min(height - 1, y + dy));
              for (let dx = -1; dx <= 1; dx++) {
                const sampleX = Math.max(0, Math.min(width - 1, x + dx));
                const index = getPixelIndex(sampleX, sampleY, width);
                const packed = (
                  source[index] |
                  (source[index + 1] << 8) |
                  (source[index + 2] << 16) |
                  (source[index + 3] << 24)
                ) >>> 0;
                const neighbor = (dy + 1) * 3 + dx + 1;
                result = mix32(result ^ packed ^ Math.imul(neighbor, 0x9e3779b1));
              }
            }
            return result;
          }

          function pixelIsSmooth(source, width, height, x, y) {
            const min = [255, 255, 255];
            const max = [0, 0, 0];

            for (let dy = -1; dy <= 1; dy++) {
              const sampleY = Math.max(0, Math.min(height - 1, y + dy));
              for (let dx = -1; dx <= 1; dx++) {
                const sampleX = Math.max(0, Math.min(width - 1, x + dx));
                const index = getPixelIndex(sampleX, sampleY, width);

                for (let channel = 0; channel < 3; channel++) {
                  const value = source[index + channel];
                  min[channel] = Math.min(min[channel], value);
                  max[channel] = Math.max(max[channel], value);
                }
              }
            }

            return max[0] - min[0] <= 2 &&
              max[1] - min[1] <= 2 &&
              max[2] - min[2] <= 2;
          }

          function applyCanvasNoise(data, width, height, offsetX = 0, offsetY = 0) {
            if (!(data instanceof Uint8ClampedArray || data instanceof Uint8Array) ||
                !width || !height || width * height > maxCanvasPixels)
              return;

            const source = new Uint8Array(data);

            for (let y = 0; y < height; y++) {
              for (let x = 0; x < width; x++) {
                const index = getPixelIndex(x, y, width);
                const red = source[index];
                const green = source[index + 1];
                const blue = source[index + 2];
                const alpha = source[index + 3];

                if (red === 0 && green === 0 && blue === 0 && alpha === 0)
                  continue;

                const packedPixel = (
                  red |
                  (green << 8) |
                  (blue << 16) |
                  (alpha << 24)
                ) >>> 0;
                const contentSeed = neighborhoodContentSeed(
                  source,
                  width,
                  height,
                  x,
                  y,
                  mix32(canvasToken ^ packedPixel)
                );
                const packedX = (x + offsetX) >>> 0;
                const packedY = (y + offsetY) >>> 0;
                const coordinateSeed = mix32(
                  canvasToken ^
                  mix32(packedX ^ 0x9e3779b9) ^
                  mix32(packedY ^ 0x85ebca6b) ^
                  contentSeed
                );

                const smooth = pixelIsSmooth(source, width, height, x, y);

                if (smooth) {
                  if ((coordinateSeed & 3) === 0) {
                    const channel = (contentSeed >>> 3) % 3;
                    const delta = (coordinateSeed & 0x100) === 0 ? -1 : 1;
                    data[index + channel] = Math.max(
                      0,
                      Math.min(255, source[index + channel] + delta)
                    );
                  }
                } else {
                  for (let channel = 0; channel < 3; channel++) {
                    const channelSeed = mix32(
                      coordinateSeed ^ Math.imul(channel + 1, 0x9e3779b1)
                    );
                    const shared = (channelSeed & 1) === 0 ? -1 : 1;
                    const independent = ((channelSeed >>> 2) % 3) - 1;
                    data[index + channel] = Math.max(
                      0,
                      Math.min(255, source[index + channel] + shared + independent)
                    );
                  }
                }

                if (alpha > 0 && alpha < 255) {
                  const alphaDelta = ((coordinateSeed >>> 9) % 3) - 1;
                  data[index + 3] = Math.max(1, Math.min(255, alpha + alphaDelta));
                }
              }
            }
          }

          if (canvasProtectionEnabled) {
            const canvasNativeMethods = new WeakMap();
            const canvasContextPrototypes = new Set([
              typeof CanvasRenderingContext2D !== 'undefined'
                ? CanvasRenderingContext2D.prototype
                : null,
              typeof OffscreenCanvasRenderingContext2D !== 'undefined'
                ? OffscreenCanvasRenderingContext2D.prototype
                : null
            ].filter(Boolean));

            for (const prototype of canvasContextPrototypes) {
              const getDescriptor = Object.getOwnPropertyDescriptor(prototype, 'getImageData');
              const putDescriptor = Object.getOwnPropertyDescriptor(prototype, 'putImageData');

              if (getDescriptor?.value && putDescriptor?.value) {
                canvasNativeMethods.set(prototype, {
                  getImageData: getDescriptor.value,
                  putImageData: putDescriptor.value
                });
              }

              wrapMethod(prototype, 'getImageData', original => function (...args) {
                const imageData = Reflect.apply(original, this, args);
                applyCanvasNoise(
                  imageData.data,
                  imageData.width,
                  imageData.height,
                  Number(args[0]) || 0,
                  Number(args[1]) || 0
                );
                return imageData;
              });
            }

            function createNoisedCanvas(source) {
              try {
                const width = source.width;
                const height = source.height;
                if (!width || !height || width * height > maxCanvasPixels)
                  return null;

                const isOffscreen = typeof OffscreenCanvas !== 'undefined' &&
                  source instanceof OffscreenCanvas;
                const copy = isOffscreen
                  ? new OffscreenCanvas(width, height)
                  : document.createElement('canvas');

                copy.width = width;
                copy.height = height;

                const context = copy.getContext('2d', { willReadFrequently: true });
                if (!context)
                  return null;

                let prototype = Object.getPrototypeOf(context);
                let nativeMethods = null;
                while (prototype && !nativeMethods) {
                  nativeMethods = canvasNativeMethods.get(prototype) ?? null;
                  prototype = Object.getPrototypeOf(prototype);
                }
                if (!nativeMethods)
                  return null;

                context.drawImage(source, 0, 0);
                const imageData = Reflect.apply(
                  nativeMethods.getImageData,
                  context,
                  [0, 0, width, height]
                );
                applyCanvasNoise(imageData.data, width, height);
                Reflect.apply(nativeMethods.putImageData, context, [imageData, 0, 0]);
                return copy;
              } catch (_) {
                return null;
              }
            }

            if (typeof HTMLCanvasElement !== 'undefined') {
              wrapMethod(HTMLCanvasElement.prototype, 'toDataURL', original => function (...args) {
                const copy = createNoisedCanvas(this);
                return Reflect.apply(original, copy || this, args);
              });

              wrapMethod(HTMLCanvasElement.prototype, 'toBlob', original => function (...args) {
                const copy = createNoisedCanvas(this);
                return Reflect.apply(original, copy || this, args);
              });
            }

            if (typeof OffscreenCanvas !== 'undefined') {
              wrapMethod(OffscreenCanvas.prototype, 'convertToBlob', original => function (...args) {
                const copy = createNoisedCanvas(this);
                return Reflect.apply(original, copy || this, args);
              });
            }
          }

          const noisedAudioBuffers = new WeakSet();
          const addNativeEventListener = EventTarget.prototype.addEventListener;
          const removeNativeEventListener = EventTarget.prototype.removeEventListener;

          function noiseRenderedAudio(buffer) {
            if (typeof AudioBuffer === 'undefined' ||
                !(buffer instanceof AudioBuffer) ||
                noisedAudioBuffers.has(buffer))
              return buffer;

            const channels = [];
            let contentSeed = mix32(audioToken ^ buffer.length ^ buffer.numberOfChannels);
            const scratch = new ArrayBuffer(4);
            const scratchView = new DataView(scratch);

            for (let channel = 0; channel < buffer.numberOfChannels; channel++) {
              const samples = buffer.getChannelData(channel);
              channels.push(samples);

              for (let i = 0; i < samples.length; i++) {
                scratchView.setFloat32(0, samples[i], true);
                contentSeed = mix32(
                  contentSeed ^ scratchView.getUint32(0, true) ^ Math.imul(i + 1, channel + 1)
                );
              }
            }

            noisedAudioBuffers.add(buffer);

            for (let channel = 0; channel < channels.length; channel++) {
              const samples = channels[channel];

              for (let i = 0; i < samples.length; i++) {
                const sampleSeed = mix32(
                  audioToken ^ contentSeed ^ Math.imul(i + 1, 0x9e3779b1) ^ Math.imul(channel + 1, 0x85ebca6b)
                );
                const unit = sampleSeed / 0xffffffff;
                const jitter = (unit * 2 - 1) * 0.0001;
                samples[i] *= 1 + jitter;
              }
            }

            return buffer;
          }

          function protectOfflineAudio(prototype) {
            wrapMethod(prototype, 'startRendering', original => function (...args) {
              const context = this;
              const onComplete = event => {
                try {
                  noiseRenderedAudio(event.renderedBuffer);
                } catch (_) { }
              };

              Reflect.apply(addNativeEventListener, context, [
                'complete',
                onComplete,
                { capture: true, once: true }
              ]);

              let result;
              try {
                result = Reflect.apply(original, context, args);
              } catch (error) {
                Reflect.apply(removeNativeEventListener, context, [
                  'complete', onComplete, true
                ]);
                throw error;
              }

              return Promise.resolve(result).then(buffer => {
                try {
                  return noiseRenderedAudio(buffer);
                } catch (_) {
                  return buffer;
                }
              }, error => {
                Reflect.apply(removeNativeEventListener, context, [
                  'complete', onComplete, true
                ]);
                throw error;
              });
            });
          }

          if (typeof OfflineAudioContext !== 'undefined')
            protectOfflineAudio(OfflineAudioContext.prototype);

          if (typeof webkitOfflineAudioContext !== 'undefined' &&
              webkitOfflineAudioContext !== OfflineAudioContext)
            protectOfflineAudio(webkitOfflineAudioContext.prototype);

          if (typeof AudioContext !== 'undefined') {
            const latency = Object.getOwnPropertyDescriptor(
              AudioContext.prototype,
              'baseLatency'
            );
            if (latency?.get) {
              defineGetter(AudioContext.prototype, 'baseLatency', function () {
                return Math.round(latency.get.call(this) * 100) / 100;
              });
            }
          }

          function randomUnit() {
            try {
              const value = new Uint32Array(1);
              crypto.getRandomValues(value);
              return value[0] / 0x100000000;
            } catch (_) {
              return Math.random();
            }
          }

          const rectScaleX = 1 + (randomUnit() - 0.5) * 0.000006;
          const rectScaleY = 1 + (randomUnit() - 0.5) * 0.000006;

          function scaleRect(rect) {
            if (!rect || typeof DOMRect === 'undefined')
              return rect;

            return new DOMRect(
              rect.x * rectScaleX,
              rect.y * rectScaleY,
              rect.width * rectScaleX,
              rect.height * rectScaleY
            );
          }

          function scaleRectList(rectList) {
            if (!rectList)
              return rectList;

            return new Proxy(rectList, {
              get(target, property) {
                if (property === 'item') {
                  return index => scaleRect(target.item(index));
                }

                if (property === Symbol.iterator) {
                  return function* () {
                    for (let index = 0; index < target.length; index++)
                      yield scaleRect(target.item(index));
                  };
                }

                if (typeof property === 'string' && /^(0|[1-9][0-9]*)$/.test(property))
                  return scaleRect(target[Number(property)]);

                const value = Reflect.get(target, property, target);
                return typeof value === 'function' ? value.bind(target) : value;
              }
            });
          }

          for (const prototype of [
            typeof Element !== 'undefined' ? Element.prototype : null,
            typeof Range !== 'undefined' ? Range.prototype : null
          ].filter(Boolean)) {
            wrapMethod(prototype, 'getBoundingClientRect', original => function (...args) {
              return scaleRect(Reflect.apply(original, this, args));
            });

            wrapMethod(prototype, 'getClientRects', original => function (...args) {
              return scaleRectList(Reflect.apply(original, this, args));
            });
          }

          const horizontalMetrics = new Set([
            'width',
            'actualBoundingBoxLeft',
            'actualBoundingBoxRight'
          ]);
          const verticalMetrics = new Set([
            'fontBoundingBoxAscent',
            'fontBoundingBoxDescent',
            'actualBoundingBoxAscent',
            'actualBoundingBoxDescent',
            'emHeightAscent',
            'emHeightDescent',
            'alphabeticBaseline',
            'hangingBaseline',
            'ideographicBaseline'
          ]);

          function scaleTextMetrics(metrics) {
            return new Proxy(metrics, {
              get(target, property) {
                const value = Reflect.get(target, property, target);
                if (typeof value !== 'number')
                  return typeof value === 'function' ? value.bind(target) : value;

                if (horizontalMetrics.has(property))
                  return value * rectScaleX;
                if (verticalMetrics.has(property))
                  return value * rectScaleY;
                return value;
              }
            });
          }

          if (canvasProtectionEnabled) {
            for (const prototype of [
              typeof CanvasRenderingContext2D !== 'undefined'
                ? CanvasRenderingContext2D.prototype
                : null,
              typeof OffscreenCanvasRenderingContext2D !== 'undefined'
                ? OffscreenCanvasRenderingContext2D.prototype
                : null
            ].filter(Boolean)) {
              wrapMethod(prototype, 'measureText', original => function (...args) {
                return scaleTextMetrics(Reflect.apply(original, this, args));
              });
            }
          }
        })();
        """;

    private sealed class CoreState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public Task<string?> OriginalUserAgentMetadataTask { get; set; } =
            Task.FromResult<string?>(null);

        public string? ScriptId { get; set; }

        // Whether the installed script includes the canvas protection.
        public bool ScriptCanvasProtection { get; set; }

        public bool NavigationProtected { get; set; }

        public bool DocumentProtected { get; set; }

        public bool NavigationCanvasProtected { get; set; }

        public bool DocumentCanvasProtected { get; set; }

        public string? TimezoneOverrideId { get; set; }

        public string? NavigationTimezoneOverrideId { get; set; }

        public string? DocumentTimezoneOverrideId { get; set; }

        public bool TimezoneOverrideInitialized { get; set; }

        public bool? UserAgentOverrideEnabled { get; set; }

        public bool NavigationUserAgentOverridden { get; set; }

        public bool DocumentUserAgentOverridden { get; set; }

        public string? OriginalUserAgent { get; set; }
    }

    private readonly ConditionalWeakTable<CoreWebView2, CoreState> _states = new();

    // Both variants share one seed so noise stays consistent for the session.
    private readonly string _protectionScriptWithCanvas;

    private readonly string _protectionScriptWithoutCanvas;

    private volatile bool _enabled;

    public FingerprintingProtectionService()
    {
        string seed = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        _protectionScriptWithCanvas = BuildProtectionScript(seed, canvasProtection: true);
        _protectionScriptWithoutCanvas = BuildProtectionScript(seed, canvasProtection: false);
        _enabled = GetEnabled();
    }

    private static string BuildProtectionScript(string seed, bool canvasProtection) =>
        ProtectionScriptTemplate
            .Replace("__SESSION_SEED__", seed, StringComparison.Ordinal)
            .Replace(
                "__CANVAS_PROTECTION__",
                canvasProtection ? "true" : "false",
                StringComparison.Ordinal);

    public bool Enabled => _enabled;

    public static bool GetEnabled() =>
        ApplicationData.Current.LocalSettings.Values[SettingName]
            as bool? ?? false;

    public static bool GetCanvasProtectionEnabled() =>
        ApplicationData.Current.LocalSettings.Values[CanvasProtectionSettingName]
            as bool? ?? true;

    public void SetEnabled(bool enabled) => _enabled = enabled;

    private static string GetConfiguredTimezoneId()
    {
        var settings = ApplicationData.Current.LocalSettings;
        string? timezoneId = settings.Values[TimezoneSettingName] as string;

        if (!string.IsNullOrWhiteSpace(timezoneId))
            return timezoneId.Trim();

        settings.Values[TimezoneSettingName] = DefaultTimezoneId;
        return DefaultTimezoneId;
    }

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
            state.OriginalUserAgent = core.Settings.UserAgent;
            state.OriginalUserAgentMetadataTask =
                ReadUserAgentMetadataAsync(core);

            core.PermissionRequested += OnPermissionRequested;

            core.NavigationStarting += (_, _) =>
            {
                state.NavigationProtected = state.ScriptId is not null;
                state.NavigationCanvasProtected = state.ScriptCanvasProtection;
                state.NavigationUserAgentOverridden =
                    state.UserAgentOverrideEnabled == true;
                state.NavigationTimezoneOverrideId =
                    state.TimezoneOverrideId;
            };

            core.ContentLoading += (_, _) =>
            {
                state.DocumentProtected = state.NavigationProtected;
                state.DocumentCanvasProtected = state.NavigationCanvasProtected;
                state.DocumentUserAgentOverridden =
                    state.NavigationUserAgentOverridden;
                state.DocumentTimezoneOverrideId =
                    state.NavigationTimezoneOverrideId;
            };
        }

        return SynchronizeAsync(core, state);
    }

    public bool NeedsReload(CoreWebView2 core) =>
        _states.TryGetValue(core, out CoreState? state) &&
        ((state.ScriptId is not null) != state.DocumentProtected ||
         (state.ScriptId is not null &&
          state.ScriptCanvasProtection != state.DocumentCanvasProtected) ||
         (state.UserAgentOverrideEnabled == true) != state.DocumentUserAgentOverridden ||
         !string.Equals(
             state.TimezoneOverrideId,
             state.DocumentTimezoneOverrideId,
             StringComparison.Ordinal));

    private async Task SynchronizeAsync(CoreWebView2 core, CoreState state)
    {
        await state.Gate.WaitAsync();

        try
        {
            // Re-check until the WebView matches the settings, since they
            // can change while we are awaiting.
            while (true)
            {
                bool desired = _enabled;
                bool desiredCanvas = desired && GetCanvasProtectionEnabled();
                string? desiredTimezoneId = desired
                    ? GetConfiguredTimezoneId()
                    : null;

                bool timezoneDone = state.TimezoneOverrideInitialized &&
                    state.TimezoneOverrideId == desiredTimezoneId;

                if (!timezoneDone)
                {
                    try
                    {
                        await core.CallDevToolsProtocolMethodAsync(
                            "Emulation.setTimezoneOverride",
                            JsonSerializer.Serialize(new
                            {
                                timezoneId = desiredTimezoneId ?? ""
                            }));

                        state.TimezoneOverrideId = desiredTimezoneId;
                        state.TimezoneOverrideInitialized = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"Failed to set timezone override: {ex}");
                    }
                }

                bool userAgentDone = desired == (state.UserAgentOverrideEnabled == true);

                if (!userAgentDone)
                {
                    try
                    {
                        await core.CallDevToolsProtocolMethodAsync(
                            "Emulation.setUserAgentOverride",
                            BuildUserAgentOverrideParameters(
                                state.OriginalUserAgent,
                                await state.OriginalUserAgentMetadataTask,
                                useChromeIdentity: desired));

                        state.UserAgentOverrideEnabled = desired;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"Failed to set the user-agent override: {ex}");
                    }
                }

                bool scriptDone = desired == (state.ScriptId is not null) &&
                    (!desired || state.ScriptCanvasProtection == desiredCanvas);

                if (!scriptDone)
                {
                    if (desired)
                    {
                        // Install the new script before removing the old one so
                        // there is no window where a navigation is unprotected.
                        string? previousScriptId = state.ScriptId;

                        state.ScriptId =
                            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                                desiredCanvas
                                    ? _protectionScriptWithCanvas
                                    : _protectionScriptWithoutCanvas);
                        state.ScriptCanvasProtection = desiredCanvas;

                        if (previousScriptId is not null)
                        {
                            core.RemoveScriptToExecuteOnDocumentCreated(
                                previousScriptId);
                        }
                    }
                    else
                    {
                        core.RemoveScriptToExecuteOnDocumentCreated(
                            state.ScriptId!);
                        state.ScriptId = null;
                        state.ScriptCanvasProtection = false;
                    }
                }

                // Stop once nothing was changed (or a call keeps failing).
                if (timezoneDone && userAgentDone && scriptDone)
                    return;
                if (_enabled == desired &&
                    (!desired ||
                     (GetConfiguredTimezoneId() == desiredTimezoneId &&
                      GetCanvasProtectionEnabled() == desiredCanvas)))
                    return;
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

    private sealed class UserAgentBrand
    {
        public string brand { get; set; } = "";

        public string version { get; set; } = "";
    }

    private sealed class UserAgentMetadataValues
    {
        public UserAgentBrand[] brands { get; set; } = [];

        public UserAgentBrand[] fullVersionList { get; set; } = [];

        public string platform { get; set; } = "Windows";

        public string platformVersion { get; set; } = "10.0.0";

        public string architecture { get; set; } = "x86";

        public string model { get; set; } = "";

        public bool mobile { get; set; }

        public string bitness { get; set; } = "64";

        public bool wow64 { get; set; }
    }

    private static async Task<string?> ReadUserAgentMetadataAsync(
        CoreWebView2 core)
    {
        const string script = """
            (() => {
              const data = navigator.userAgentData;
              if (!data)
                return null;

              return {
                brands: Array.from(data.brands),
                platform: data.platform,
                mobile: data.mobile
              };
            })()
            """;

        try
        {
            string result = await core.ExecuteScriptAsync(script);
            return string.IsNullOrWhiteSpace(result) || result == "null"
                ? null
                : result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to read the original User-Agent Client Hints: {ex}");
            return null;
        }
    }

    private static string BuildUserAgentOverrideParameters(
        string? originalUserAgent,
        string? originalMetadataJson,
        bool useChromeIdentity)
    {
        string fullVersion = GetBrowserVersion(originalUserAgent);
        string majorVersion = fullVersion.Split('.')[0];
        string original = string.IsNullOrWhiteSpace(originalUserAgent)
            ? BuildFallbackEdgeUserAgent(fullVersion)
            : originalUserAgent;

        string userAgent = useChromeIdentity
            ? BuildChromeUserAgent(original, majorVersion)
            : original;

        UserAgentMetadataValues metadata = ParseUserAgentMetadata(
            originalMetadataJson,
            fullVersion,
            majorVersion);

        if (useChromeIdentity)
        {
            metadata.brands = ReplaceEdgeBrand(metadata.brands);
            metadata.fullVersionList = ReplaceEdgeBrand(metadata.fullVersionList);
        }

        var parameters = new
        {
            userAgent,
            platform = "Win32",
            userAgentMetadata = new
            {
                brands = metadata.brands,
                fullVersionList = metadata.fullVersionList,
                platform = metadata.platform,
                platformVersion = metadata.platformVersion,
                architecture = metadata.architecture,
                model = metadata.model,
                mobile = metadata.mobile,
                bitness = metadata.bitness,
                wow64 = metadata.wow64
            }
        };

        return JsonSerializer.Serialize(parameters);
    }

    private static UserAgentMetadataValues ParseUserAgentMetadata(
        string? metadataJson,
        string fullVersion,
        string majorVersion)
    {
        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            try
            {
                UserAgentMetadataValues? metadata =
                    JsonSerializer.Deserialize<UserAgentMetadataValues>(metadataJson,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                if (metadata?.brands is { Length: > 0 })
                {
                    metadata.fullVersionList = BuildFullVersionList(
                        metadata.brands,
                        fullVersion);
                    return metadata;
                }
            }
            catch (JsonException ex)
            {
                Debug.WriteLine(
                    $"Failed to parse original User-Agent Client Hints: {ex}");
            }
        }

        string architecture = RuntimeInformation.ProcessArchitecture is
            Architecture.Arm or Architecture.Arm64
                ? "arm"
                : "x86";
        string bitness = Environment.Is64BitProcess ? "64" : "32";

        return new UserAgentMetadataValues
        {
            brands =
            [
                new() { brand = "Not_A Brand", version = "99" },
                new() { brand = "Chromium", version = majorVersion },
                new() { brand = "Microsoft Edge", version = majorVersion }
            ],
            fullVersionList =
            [
                new() { brand = "Not_A Brand", version = "99.0.0.0" },
                new() { brand = "Chromium", version = fullVersion },
                new() { brand = "Microsoft Edge", version = fullVersion }
            ],
            platform = "Windows",
            platformVersion = "10.0.0",
            architecture = architecture,
            model = "",
            mobile = false,
            bitness = bitness,
            wow64 = false
        };
    }

    private static UserAgentBrand[] BuildFullVersionList(
        UserAgentBrand[] brands,
        string fullVersion) =>
        brands.Select(item => new UserAgentBrand
        {
            brand = item.brand,
            version = item.brand.Contains("Brand", StringComparison.Ordinal)
                ? $"{item.version}.0.0.0"
                : fullVersion
        }).ToArray();

    private static UserAgentBrand[] ReplaceEdgeBrand(
        UserAgentBrand[] brands) =>
        brands.Select(item => new UserAgentBrand
        {
            brand = string.Equals(
                item.brand,
                "Microsoft Edge",
                StringComparison.Ordinal)
                    ? "Google Chrome"
                    : item.brand,
            version = item.version
        }).ToArray();

    private static string GetBrowserVersion(string? userAgent)
    {
        string version = App.WebViewEnvironment?.BrowserVersionString ?? "";
        int suffix = version.IndexOf(' ');
        if (suffix >= 0)
            version = version[..suffix];

        if (!Version.TryParse(version, out Version? parsed))
        {
            foreach (string marker in new[] { "Edg/", "Chrome/" })
            {
                int markerIndex = userAgent?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
                if (markerIndex < 0)
                    continue;

                int versionStart = markerIndex + marker.Length;
                int versionEnd = userAgent!.IndexOf(' ', versionStart);
                version = versionEnd < 0
                    ? userAgent[versionStart..]
                    : userAgent[versionStart..versionEnd];

                if (Version.TryParse(version, out parsed))
                    break;
            }
        }

        if (parsed is null)
            return "0.0.0.0";

        int build = Math.Max(0, parsed.Build);
        int revision = Math.Max(0, parsed.Revision);
        return $"{parsed.Major}.{Math.Max(0, parsed.Minor)}.{build}.{revision}";
    }

    private static string BuildChromeUserAgent(
        string originalUserAgent,
        string majorVersion)
    {
        string result = originalUserAgent;
        string chromeMarker = "Chrome/";
        int chromeIndex = result.IndexOf(chromeMarker, StringComparison.Ordinal);

        if (chromeIndex >= 0)
        {
            int end = result.IndexOf(' ', chromeIndex);
            if (end < 0)
                end = result.Length;

            result = result[..chromeIndex] +
                $"Chrome/{majorVersion}.0.0.0" +
                result[end..];
        }

        int edgeIndex = result.IndexOf(" Edg/", StringComparison.Ordinal);
        if (edgeIndex >= 0)
            result = result[..edgeIndex];

        if (!result.Contains("Chrome/", StringComparison.Ordinal))
            return BuildFallbackChromeUserAgent(majorVersion);

        return result;
    }

    private static string BuildFallbackEdgeUserAgent(string version) =>
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        $"(KHTML, like Gecko) Chrome/{version} Safari/537.36 Edg/{version}";

    private static string BuildFallbackChromeUserAgent(string majorVersion) =>
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        $"(KHTML, like Gecko) Chrome/{majorVersion}.0.0.0 Safari/537.36";

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