using Microsoft.Web.WebView2.Core;
using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private ITaskbarList3? _taskbarList;

    private void UpdateTaskbarDownloadProgress()
    {
        DownloadItem[] activeDownloads = Downloads
            .Where(item => item.State == CoreWebView2DownloadState.InProgress)
            .ToArray();

        if (activeDownloads.Length == 0)
        {
            SetTaskbarProgressState(TaskbarProgressState.NoProgress);
            return;
        }

        if (!TryGetTaskbarList(out ITaskbarList3 taskbar))
            return;

        nint windowHandle = GetWindowHandle();

        if (activeDownloads.Any(item => item.TotalBytesToReceive <= 0))
        {
            taskbar.SetProgressState(
                windowHandle,
                TaskbarProgressState.Indeterminate);
            return;
        }

        ulong bytesReceived = 0;
        ulong totalBytes = 0;

        foreach (DownloadItem item in activeDownloads)
        {
            ulong total = (ulong)item.TotalBytesToReceive;

            ulong received = (ulong)Math.Clamp(
                item.BytesReceived,
                0,
                item.TotalBytesToReceive);

            bytesReceived += received;
            totalBytes += total;
        }

        if (totalBytes == 0)
        {
            taskbar.SetProgressState(
                windowHandle,
                TaskbarProgressState.Indeterminate);
            return;
        }

        taskbar.SetProgressState(
            windowHandle,
            TaskbarProgressState.Normal);

        taskbar.SetProgressValue(
            windowHandle,
            bytesReceived,
            totalBytes);
    }

    private void SetTaskbarProgressState(TaskbarProgressState state)
    {
        if (_taskbarList is null || GetWindowHandle() == 0)
            return;

        try
        {
            _taskbarList.SetProgressState(GetWindowHandle(), state);
        }
        catch (COMException)
        {
            _taskbarList = null;
        }
    }

    private bool TryGetTaskbarList(out ITaskbarList3 taskbar)
    {
        if (_taskbarList is ITaskbarList3 existingTaskbar)
        {
            taskbar = existingTaskbar;
            return true;
        }

        try
        {
            ITaskbarList3 createdTaskbar = (ITaskbarList3)new CTaskbarList();

            if (createdTaskbar.HrInit() < 0)
            {
                taskbar = null!;
                return false;
            }

            _taskbarList = createdTaskbar;
            taskbar = createdTaskbar;
            return true;
        }
        catch (COMException)
        {
            taskbar = null!;
            return false;
        }
    }

    [Flags]
    private enum TaskbarProgressState
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig]
        int HrInit();

        [PreserveSig]
        int AddTab(nint windowHandle);

        [PreserveSig]
        int DeleteTab(nint windowHandle);

        [PreserveSig]
        int ActivateTab(nint windowHandle);

        [PreserveSig]
        int SetActiveAlt(nint windowHandle);

        [PreserveSig]
        int MarkFullscreenWindow(nint windowHandle, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        [PreserveSig]
        int SetProgressValue(nint windowHandle, ulong completed, ulong total);

        [PreserveSig]
        int SetProgressState(nint windowHandle, TaskbarProgressState state);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private class CTaskbarList
    {
    }
}
