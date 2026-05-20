using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DeviceRemover
{
    public record DeviceInfo(
        string InstanceId,
        string FriendlyName,
        string Description,
        string[] HardwareIds,
        Guid ClassGuid,
        uint DevInst,
        bool IsEnabled,
        bool IsDisableable,
        uint ProblemNumber
    );

    public static class DeviceManager
    {
        // SetupAPI constants
        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_ALLCLASSES = 0x00000004;

        private const uint SPDRP_DEVICEDESC = 0x00000000;
        private const uint SPDRP_HARDWAREID = 0x00000001;
        private const uint SPDRP_FRIENDLYNAME = 0x0000000C;

        private const uint DIF_PROPERTYCHANGE = 0x00000012;
        private const uint DICS_ENABLE = 1;
        private const uint DICS_DISABLE = 2;
        private const uint DICS_FLAG_GLOBAL = 1;

        // Cfgmgr32 constants
        private const uint DN_STARTED = 0x00000008;
        private const uint DN_DISABLEABLE = 0x00002000;
        private const uint DN_HAS_PROBLEM = 0x00000400;
        private const uint CM_PROB_DISABLED = 22;

        // SetupAPI reboot constants
        private const uint DI_NEEDREBOOT = 0x00000001;
        private const uint DI_NEEDRESTART = 0x00000002;

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        #region Structs

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid classGuid;
            public uint devInst;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_CLASSINSTALL_HEADER
        {
            public uint cbSize;
            public uint installFunction;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_PROPCHANGE_PARAMS
        {
            public SP_CLASSINSTALL_HEADER classInstallHeader;
            public uint stateChange;
            public uint scope;
            public uint hwProfile;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
        public struct SP_DEVINSTALL_PARAMS
        {
            public uint cbSize;
            public uint flags;
            public uint flagsEx;
            public IntPtr hwndParent;
            public IntPtr installMsgHandler;
            public IntPtr installMsgHandlerContext;
            public IntPtr fileQueue;
            public UIntPtr classInstallReserved;
            public uint reserved;
            
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string driverPath;
        }

        #endregion

        #region P/Invokes

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(
            ref Guid classGuid,
            [MarshalAs(UnmanagedType.LPWStr)] string? enumerator,
            IntPtr hwndParent,
            uint flags
        );

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(
            IntPtr deviceInfoSet,
            uint memberIndex,
            ref SP_DEVINFO_DATA deviceInfoData
        );

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static unsafe extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            char* deviceInstanceId,
            int deviceInstanceIdSize,
            out int requiredSize
        );

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static unsafe extern bool SetupDiGetDeviceRegistryPropertyW(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            uint property,
            out uint propertyRegDataType,
            byte* propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize
        );

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiSetClassInstallParamsW(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            ref SP_PROPCHANGE_PARAMS classInstallParams,
            uint classInstallParamsSize
        );

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(
            uint installFunction,
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData
        );

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInstallParamsW(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            ref SP_DEVINSTALL_PARAMS deviceInstallParams
        );

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        private static extern int CM_Get_DevNode_Status(
            out uint pulStatus,
            out uint pulProblemNumber,
            uint dnDevInst,
            uint ulFlags
        );

        #endregion

        public static List<DeviceInfo> EnumerateDevices()
        {
            var list = new List<DeviceInfo>(128);
            Guid emptyGuid = Guid.Empty;
            
            // Get all present devices
            IntPtr hDevInfo = SetupDiGetClassDevsW(
                ref emptyGuid,
                null,
                IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_ALLCLASSES
            );

            if (hDevInfo == INVALID_HANDLE_VALUE)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var devInfoData = new SP_DEVINFO_DATA();
                devInfoData.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

                uint i = 0;
                while (SetupDiEnumDeviceInfo(hDevInfo, i, ref devInfoData))
                {
                    string instanceId = GetDeviceInstanceId(hDevInfo, ref devInfoData);
                    string friendlyName = GetDeviceProperty(hDevInfo, ref devInfoData, SPDRP_FRIENDLYNAME) ?? string.Empty;
                    string description = GetDeviceProperty(hDevInfo, ref devInfoData, SPDRP_DEVICEDESC) ?? string.Empty;
                    string[] hardwareIds = GetDeviceMultiStringProperty(hDevInfo, ref devInfoData, SPDRP_HARDWAREID);
                    
                    bool isEnabled = GetDeviceStatus(devInfoData.devInst, out bool isDisableable, out uint problemNumber);

                    list.Add(new DeviceInfo(
                        InstanceId: instanceId,
                        FriendlyName: friendlyName,
                        Description: description,
                        HardwareIds: hardwareIds,
                        ClassGuid: devInfoData.classGuid,
                        DevInst: devInfoData.devInst,
                        IsEnabled: isEnabled,
                        IsDisableable: isDisableable,
                        ProblemNumber: problemNumber
                    ));

                    i++;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hDevInfo);
            }

            return list;
        }

        public static bool ChangeDeviceState(string instanceId, bool enable, out bool rebootRequired)
        {
            rebootRequired = false;
            Guid emptyGuid = Guid.Empty;

            IntPtr hDevInfo = SetupDiGetClassDevsW(
                ref emptyGuid,
                null,
                IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_ALLCLASSES
            );

            if (hDevInfo == INVALID_HANDLE_VALUE)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var devInfoData = new SP_DEVINFO_DATA();
                devInfoData.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

                uint i = 0;
                while (SetupDiEnumDeviceInfo(hDevInfo, i, ref devInfoData))
                {
                    string currentId = GetDeviceInstanceId(hDevInfo, ref devInfoData);
                    if (string.Equals(currentId, instanceId, StringComparison.OrdinalIgnoreCase))
                    {
                        // Found the target device!
                        
                        // 1. Prepare parameters
                        var pcp = new SP_PROPCHANGE_PARAMS();
                        pcp.classInstallHeader.cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>();
                        pcp.classInstallHeader.installFunction = DIF_PROPERTYCHANGE;
                        pcp.stateChange = enable ? DICS_ENABLE : DICS_DISABLE;
                        pcp.scope = DICS_FLAG_GLOBAL;
                        pcp.hwProfile = 0;

                        // 2. Set class install parameters
                        if (!SetupDiSetClassInstallParamsW(hDevInfo, ref devInfoData, ref pcp, (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()))
                        {
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                        }

                        // 3. Call class installer
                        if (!SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, hDevInfo, ref devInfoData))
                        {
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                        }

                        // 4. Check if reboot is required
                        var devParams = new SP_DEVINSTALL_PARAMS();
                        devParams.cbSize = (uint)Marshal.SizeOf<SP_DEVINSTALL_PARAMS>();
                        if (SetupDiGetDeviceInstallParamsW(hDevInfo, ref devInfoData, ref devParams))
                        {
                            rebootRequired = (devParams.flags & (DI_NEEDREBOOT | DI_NEEDRESTART)) != 0;
                        }

                        return true;
                    }
                    i++;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hDevInfo);
            }

            return false; // Device not found
        }

        #region Helper Methods

        private static unsafe string GetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData)
        {
            int requiredSize = 0;
            // Get required size
            SetupDiGetDeviceInstanceIdW(deviceInfoSet, ref deviceInfoData, null, 0, out requiredSize);

            if (requiredSize == 0)
            {
                return string.Empty;
            }

            char* stackBuffer = stackalloc char[requiredSize];
            if (SetupDiGetDeviceInstanceIdW(deviceInfoSet, ref deviceInfoData, stackBuffer, requiredSize, out _))
            {
                return new string(stackBuffer, 0, requiredSize).TrimEnd('\0');
            }

            return string.Empty;
        }

        private static unsafe string? GetDeviceProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property)
        {
            uint regType;
            uint requiredSize = 0;

            // Get size
            SetupDiGetDeviceRegistryPropertyW(deviceInfoSet, ref deviceInfoData, property, out regType, null, 0, out requiredSize);

            if (requiredSize == 0)
            {
                return null;
            }

            byte[]? poolBuffer = null;
            byte* bufferPtr;

            // Allocate stack for small strings (<= 512 chars / 1024 bytes) to avoid heap allocations
            if (requiredSize <= 1024)
            {
                byte* stackBuffer = stackalloc byte[(int)requiredSize];
                bufferPtr = stackBuffer;
            }
            else
            {
                poolBuffer = ArrayPool<byte>.Shared.Rent((int)requiredSize);
                fixed (byte* p = poolBuffer)
                {
                    bufferPtr = p;
                }
            }

            try
            {
                if (SetupDiGetDeviceRegistryPropertyW(deviceInfoSet, ref deviceInfoData, property, out regType, bufferPtr, requiredSize, out _))
                {
                    // Unicode is 2 bytes per char
                    int charCount = (int)(requiredSize / 2);
                    return new string((char*)bufferPtr, 0, charCount).TrimEnd('\0');
                }
            }
            finally
            {
                if (poolBuffer != null)
                {
                    ArrayPool<byte>.Shared.Return(poolBuffer);
                }
            }

            return null;
        }

        private static string[] GetDeviceMultiStringProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property)
        {
            string? raw = GetDeviceProperty(deviceInfoSet, ref deviceInfoData, property);
            if (string.IsNullOrEmpty(raw))
            {
                return Array.Empty<string>();
            }

            return raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool GetDeviceStatus(uint devInst, out bool isDisableable, out uint problemNumber)
        {
            uint status = 0;
            problemNumber = 0;
            isDisableable = false;

            int result = CM_Get_DevNode_Status(out status, out problemNumber, devInst, 0);
            if (result == 0) // CR_SUCCESS
            {
                isDisableable = (status & DN_DISABLEABLE) != 0;
                bool isStarted = (status & DN_STARTED) != 0;
                bool hasProblem = (status & DN_HAS_PROBLEM) != 0;

                if (hasProblem && problemNumber == CM_PROB_DISABLED)
                {
                    return false; // Device is disabled
                }

                return isStarted; // Active if started
            }

            return false;
        }

        public static bool IsMatch(DeviceInfo device, AliasConfig alias)
        {
            if (string.Equals(alias.MatchField, "InstanceId", StringComparison.OrdinalIgnoreCase))
            {
                return IsMatchValue(device.InstanceId, alias.Value, alias.MatchMode);
            }
            else if (string.Equals(alias.MatchField, "HardwareId", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var hwId in device.HardwareIds)
                {
                    if (IsMatchValue(hwId, alias.Value, alias.MatchMode))
                        return true;
                }
                return false;
            }
            else if (string.Equals(alias.MatchField, "FriendlyName", StringComparison.OrdinalIgnoreCase))
            {
                return IsMatchValue(device.FriendlyName, alias.Value, alias.MatchMode) ||
                       IsMatchValue(device.Description, alias.Value, alias.MatchMode);
            }
            else // Any / Default
            {
                if (IsMatchValue(device.InstanceId, alias.Value, alias.MatchMode))
                    return true;
                if (IsMatchValue(device.FriendlyName, alias.Value, alias.MatchMode))
                    return true;
                if (IsMatchValue(device.Description, alias.Value, alias.MatchMode))
                    return true;
                foreach (var hwId in device.HardwareIds)
                {
                    if (IsMatchValue(hwId, alias.Value, alias.MatchMode))
                        return true;
                }
                return false;
            }
        }

        private static bool IsMatchValue(string text, string pattern, string mode)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
                return false;

            if (string.Equals(mode, "Exact", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);
            }
            else if (string.Equals(mode, "Regex", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
            else if (string.Equals(mode, "Wildcard", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                        .Replace("\\*", ".*")
                        .Replace("\\?", ".") + "$";
                    return System.Text.RegularExpressions.Regex.IsMatch(text, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
            else // Contains
            {
                return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            }
        }

        #endregion
    }
}
