using System.Runtime.InteropServices;

namespace EmulatorShell;

/// The menu a right click on the app's taskbar button shows: a "模拟器管理器" task and a "设备" category with each
/// AVD, which start this app without arguments or with the AVD's name, as the Start menu and the SDK forwarder do.
///
/// Built with the shell's ICustomDestinationList, called through its vtable: the app registers CsWinRT's
/// ComWrappers for marshalling, which classic [ComImport] interfaces do not reliably cast through.
static unsafe class JumpList
{
    private static readonly Guid CLSID_DestinationList = new("77f10cf0-3db5-4966-b520-b7c54fd35ed6");
    private static readonly Guid IID_ICustomDestinationList = new("6332debf-87b5-4670-90c0-5e57b408a49e");
    private static readonly Guid CLSID_EnumerableObjectCollection = new("2d3468c1-36a7-43b6-ac24-d3f02fd9607a");
    private static readonly Guid IID_IObjectCollection = new("5632b1a4-e38a-400a-928a-d4cd63230295");
    private static readonly Guid IID_IObjectArray = new("92ca9dcd-5622-4bba-a805-5e9f541bd8c9");
    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-c000-000000000046");
    private static readonly Guid IID_IShellLinkW = new("000214f9-0000-0000-c000-000000000046");
    private static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    private static readonly Guid FMTID_Title = new("f29f85e0-4ff9-1068-ab91-08002b27b3d9");
    private const int PID_Title = 2;
    private const uint CLSCTX_INPROC_SERVER = 1;

    /// Replaces the list; `avds` are (id, name) pairs in the order the manager shows them.
    public static void Update(IReadOnlyList<(string Id, string Name)> avds)
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        nint list = 0, tasks = 0, devices = 0, removed = 0;
        try
        {
            list = Create(CLSID_DestinationList, IID_ICustomDestinationList);
            uint slots;
            var arrayIid = IID_IObjectArray;
            Check(Call<uint, Guid, nint>(list, 4, &slots, &arrayIid, &removed), "BeginList");

            tasks = Create(CLSID_EnumerableObjectCollection, IID_IObjectCollection);
            AddLink(tasks, exe, "", "模拟器管理器", "打开 Emulator Shell 的设备管理器");
            Check(Call(list, 7, QueryArray(tasks)), "AddUserTasks");

            if (avds.Count > 0)
            {
                devices = Create(CLSID_EnumerableObjectCollection, IID_IObjectCollection);
                foreach (var (id, name) in avds) AddLink(devices, exe, Quote(id), name, $"打开 {name}");
                fixed (char* category = "设备")
                {
                    // Fails if the user removed one of its items from the list; the tasks are still worth keeping.
                    var result = Call(list, 5, (nint)category, QueryArray(devices));
                    if (result < 0) Log.Debug($"jump list: AppendCategory 0x{result:X8}");
                }
            }
            Check(Call(list, 8), "CommitList");
        }
        catch (Exception error)
        {
            Log.Error($"cannot update the jump list: {error.Message}");
            if (list != 0) Call(list, 11);
        }
        finally
        {
            foreach (var pointer in new[] { removed, devices, tasks, list }) if (pointer != 0) Marshal.Release(pointer);
        }
    }

    private static string Quote(string argument) => argument.Contains(' ') ? $"\"{argument}\"" : argument;

    private static void AddLink(nint collection, string exe, string arguments, string title, string description)
    {
        var link = Create(CLSID_ShellLink, IID_IShellLinkW);
        nint store = 0;
        try
        {
            fixed (char* path = exe, args = arguments, tip = description)
            {
                Check(Call(link, 20, (nint)path), "SetPath");
                Check(Call(link, 11, (nint)args), "SetArguments");
                Check(Call(link, 7, (nint)tip), "SetDescription");
                Check(Call(link, 17, (nint)path, 0), "SetIconLocation");
            }
            var storeIid = IID_IPropertyStore;
            Check(Call<Guid, nint>(link, 0, &storeIid, &store), "QueryInterface IPropertyStore");
            var key = new PropertyKey { FormatId = FMTID_Title, PropertyId = PID_Title };
            var value = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(title) };
            try
            {
                Check(Call<PropertyKey, PropVariant>(store, 6, &key, &value), "SetValue");
                Check(Call(store, 7), "Commit");
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Pointer);
            }
            Check(Call(collection, 5, link), "AddObject");
        }
        finally
        {
            if (store != 0) Marshal.Release(store);
            Marshal.Release(link);
        }
    }

    /// The collection as an IObjectArray, which the list's methods take; the reference is the collection's own
    /// (released with it), as the list only borrows it for the call.
    private static nint QueryArray(nint collection)
    {
        var iid = IID_IObjectArray;
        nint array;
        Check(Call<Guid, nint>(collection, 0, &iid, &array), "QueryInterface IObjectArray");
        Marshal.Release(array);
        return array;
    }

    private static nint Create(Guid clsid, Guid iid)
    {
        Check(CoCreateInstance(&clsid, 0, CLSCTX_INPROC_SERVER, &iid, out var instance), $"CoCreateInstance {clsid}");
        return instance;
    }

    private static void Check(int result, string what)
    {
        if (result < 0) throw new COMException(what, result);
    }

    private static nint Method(nint instance, int index) => (*(nint**)instance)[index];

    private static int Call(nint instance, int index) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Method(instance, index))(instance);

    private static int Call(nint instance, int index, nint a) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(instance, index))(instance, a);

    private static int Call(nint instance, int index, nint a, nint b) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Method(instance, index))(instance, a, b);

    private static int Call(nint instance, int index, nint a, int b) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int, int>)Method(instance, index))(instance, a, b);

    private static int Call<TA, TB>(nint instance, int index, TA* a, TB* b) where TA : unmanaged where TB : unmanaged =>
        ((delegate* unmanaged[Stdcall]<nint, TA*, TB*, int>)Method(instance, index))(instance, a, b);

    private static int Call<TA, TB, TC>(nint instance, int index, TA* a, TB* b, TC* c) where TA : unmanaged where TB : unmanaged where TC : unmanaged =>
        ((delegate* unmanaged[Stdcall]<nint, TA*, TB*, TC*, int>)Method(instance, index))(instance, a, b, c);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    /// A PROPVARIANT holding a string: the type, three reserved words, then the union, 24 bytes in all on 64-bit.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint instance);
}
