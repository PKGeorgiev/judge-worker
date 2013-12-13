﻿namespace OJS.Workers.Executors.Process
{
    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;
    using System.Security;

    using Microsoft.Win32.SafeHandles;

    [SuppressUnmanagedCodeSecurity]
    public sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private static SafeProcessHandle invalidHandle = new SafeProcessHandle(IntPtr.Zero);

        // Note that OpenProcess returns 0 on failure 
        internal SafeProcessHandle()
            : base(true)
        {
        }

        internal SafeProcessHandle(IntPtr handle)
            : base(true)
        {
            this.SetHandle(handle);
        }

        internal void InitialSetHandle(IntPtr h)
        {
            Debug.Assert(this.IsInvalid, "Safe handle should only be set once");
            this.handle = h;
        }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.CloseHandle(this.handle);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [ResourceExposure(ResourceScope.Machine)]
        private static extern SafeProcessHandle OpenProcess(int access, bool inherit, int processId);
    } 
}
