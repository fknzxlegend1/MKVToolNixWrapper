using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MKVToolNixWrapper;

internal sealed class WindowsTaskbarProgress : IDisposable
{
    private readonly ITaskbarList3 _taskbar;
    private readonly IntPtr _windowHandle;

    private WindowsTaskbarProgress(ITaskbarList3 taskbar, IntPtr windowHandle)
    {
        _taskbar = taskbar;
        _windowHandle = windowHandle;
    }

    public static WindowsTaskbarProgress? TryCreate(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var windowHandle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (windowHandle == IntPtr.Zero)
        {
            return null;
        }

        var taskbar = (ITaskbarList3)new TaskbarListComClass();
        Marshal.ThrowExceptionForHR(taskbar.HrInit());
        return new WindowsTaskbarProgress(taskbar, windowHandle);
    }

    public void SetState(TaskbarProgressState state, ulong completed = 0, ulong total = 100)
    {
        var nativeState = state switch
        {
            TaskbarProgressState.Indeterminate => TaskbarProgressBarState.Indeterminate,
            TaskbarProgressState.Normal => TaskbarProgressBarState.Normal,
            TaskbarProgressState.Error => TaskbarProgressBarState.Error,
            _ => TaskbarProgressBarState.NoProgress
        };

        Marshal.ThrowExceptionForHR(_taskbar.SetProgressState(_windowHandle, nativeState));
        if (state == TaskbarProgressState.Normal)
        {
            Marshal.ThrowExceptionForHR(_taskbar.SetProgressValue(_windowHandle, completed, total));
        }
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows() && Marshal.IsComObject(_taskbar))
        {
            Marshal.FinalReleaseComObject(_taskbar);
        }
    }

    internal enum TaskbarProgressState
    {
        None,
        Indeterminate,
        Normal,
        Error
    }

    private enum TaskbarProgressBarState
    {
        NoProgress,
        Indeterminate,
        Normal,
        Error
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListComClass
    {
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr hwnd, TaskbarProgressBarState state);
        [PreserveSig] int RegisterTab(IntPtr tab, IntPtr mdi);
        [PreserveSig] int UnregisterTab(IntPtr tab);
        [PreserveSig] int SetTabOrder(IntPtr tab, IntPtr insertBefore);
        [PreserveSig] int SetTabActive(IntPtr tab, IntPtr mdi, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint buttonCount, IntPtr buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint buttonCount, IntPtr buttons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tooltip);
        [PreserveSig] int SetThumbnailClip(IntPtr hwnd, IntPtr rectangle);
    }
}
