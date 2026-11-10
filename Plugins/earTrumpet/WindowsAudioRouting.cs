using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PersonalCards.Audio;

internal static class WindowsAudioRouting
{
    private static readonly Guid PolicyClient = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    private static readonly Guid PolicyInterface = new("f8679f50-850a-41cf-9c72-430f290290c8");
    private static readonly Guid AppPolicyInterface = new("ab3d4648-e242-459f-b02f-541c70306324");
    private static readonly Role[] PlaybackRoles = { Role.Console, Role.Multimedia };
    private const int SetDefaultEndpointSlot = 13;
    private const int SetAppEndpointSlot = 25;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetDefault(IntPtr instance, [MarshalAs(UnmanagedType.LPWStr)] string device, Role role);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetAppOutput(IntPtr instance, uint process, DataFlow flow, Role role, IntPtr device);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [DllImport("combase.dll")]
    private static extern int RoInitialize(uint mode);

    [DllImport("combase.dll")]
    private static extern void RoUninitialize();

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr className, in Guid iid, out IntPtr instance);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string text, int length, out IntPtr value);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr value);

    public static void SetSystemOutput(string deviceId)
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(PolicyClient, IntPtr.Zero, 1, PolicyInterface, out var policy));
        try
        {
            var set = GetMethod<SetDefault>(policy, SetDefaultEndpointSlot);
            foreach (var role in PlaybackRoles)
                Marshal.ThrowExceptionForHR(set(policy, deviceId, role));
        }
        finally { Marshal.Release(policy); }
    }

    public static void SetApplicationOutput(uint processId, string? deviceId)
    {
        const string className = "Windows.Media.Internal.AudioPolicyConfig";
        Marshal.ThrowExceptionForHR(RoInitialize(0));
        IntPtr factoryName = IntPtr.Zero;
        IntPtr device = IntPtr.Zero;
        IntPtr factory = IntPtr.Zero;
        try
        {
            factoryName = MakeString(className);
            if (deviceId != null)
                device = MakeString(@"\\?\SWD#MMDEVAPI#" + deviceId + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(factoryName, AppPolicyInterface, out factory));
            var set = GetMethod<SetAppOutput>(factory, SetAppEndpointSlot);
            foreach (var role in PlaybackRoles)
                Marshal.ThrowExceptionForHR(set(factory, processId, DataFlow.Render, role, device));
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (device != IntPtr.Zero) WindowsDeleteString(device);
            if (factoryName != IntPtr.Zero) WindowsDeleteString(factoryName);
            RoUninitialize();
        }
    }

    private static IntPtr MakeString(string text)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(text, text.Length, out var value));
        return value;
    }

    // These slots are the Windows audio policy ABI for Windows 11.
    private static T GetMethod<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
}
