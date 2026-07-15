using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.ApplicationModel.Background;
using WinRT;

namespace PackagePilot.Background;

internal static partial class ComServer
{
    internal const uint ClsctxLocalServer = 4;
    internal const uint RegclsSingleUse = 0;
    internal const uint S_OK = 0x00000000;
    internal const uint ClassENoAggregation = 0x80040110;
    internal const uint ENoInterface = 0x80004002;

    private const string IidIUnknown = "00000000-0000-0000-C000-000000000046";
    private const string IidIClassFactory = "00000001-0000-0000-C000-000000000046";

    [LibraryImport("ole32.dll")]
    internal static partial int CoRegisterClassObject(
        ref Guid classId,
        [MarshalAs(UnmanagedType.Interface)] IClassFactory classFactory,
        uint executionContext,
        uint flags,
        out uint registrationToken);

    [LibraryImport("ole32.dll")]
    internal static partial int CoRevokeClassObject(uint registrationToken);

    [GeneratedComInterface]
    [Guid(IidIClassFactory)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IClassFactory
    {
        [PreserveSig]
        uint CreateInstance(
            nint outerUnknown,
            in Guid interfaceId,
            out nint objectPointer);

        [PreserveSig]
        uint LockServer([MarshalAs(UnmanagedType.Bool)] bool @lock);
    }

    [GeneratedComClass]
    internal sealed partial class BackgroundTaskFactory : IClassFactory
    {
        public uint CreateInstance(
            nint outerUnknown,
            in Guid interfaceId,
            out nint objectPointer)
        {
            objectPointer = nint.Zero;
            if (outerUnknown != nint.Zero)
            {
                return ClassENoAggregation;
            }

            var backgroundTaskInterface = typeof(IBackgroundTask).GUID;
            if (interfaceId != new Guid(IidIUnknown)
                && interfaceId != backgroundTaskInterface)
            {
                return ENoInterface;
            }

            objectPointer = MarshalInterface<IBackgroundTask>.FromManaged(new BackgroundUpdateTask());
            return S_OK;
        }

        public uint LockServer(bool @lock) => S_OK;
    }
}
