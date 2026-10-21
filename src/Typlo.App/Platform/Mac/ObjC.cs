using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Typlo.App.Platform.Mac;

/// <summary>Minimal Objective-C runtime bridge (enough for NSPasteboard and NSWorkspace).</summary>
[SupportedOSPlatform("macos")]
internal static partial class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint objc_getClass(string name);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint sel_registerName(string name);
    [LibraryImport(Lib)] public static partial nint objc_autoreleasePoolPush();
    [LibraryImport(Lib)] public static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")] public static partial nint Send(nint receiver, nint sel);
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")] public static partial nint Send(nint receiver, nint sel, nint a);
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")] public static partial nint Send(nint receiver, nint sel, nint a, nint b);
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")] public static partial nint SendLong(nint receiver, nint sel, long a);
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")] public static partial nint SendUL(nint receiver, nint sel, nuint a);
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")] public static partial nint SendPtrLen(nint receiver, nint sel, nint ptr, nuint len);
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")] public static partial nint SendIntInt(nint receiver, nint sel, int a);

    [LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AXIsProcessTrusted();

    private static readonly Dictionary<string, nint> Selectors = [];
    private static readonly Dictionary<string, nint> Classes = [];

    public static nint Sel(string name)
    {
        lock (Selectors)
        {
            if (!Selectors.TryGetValue(name, out var s)) Selectors[name] = s = sel_registerName(name);
            return s;
        }
    }

    public static nint Class(string name)
    {
        lock (Classes)
        {
            if (!Classes.TryGetValue(name, out var c)) Classes[name] = c = objc_getClass(name);
            return c;
        }
    }

    public static nint Call(nint obj, string sel) => Send(obj, Sel(sel));
    public static nint Call(nint obj, string sel, nint a) => Send(obj, Sel(sel), a);
    public static nint Call(nint obj, string sel, nint a, nint b) => Send(obj, Sel(sel), a, b);

    public static nint NSString(string s)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(s);
        try { return Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public static string? ToStr(nint nsString)
    {
        if (nsString == 0) return null;
        return Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));
    }

    public static nint NSData(byte[] bytes)
    {
        var h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { return SendPtrLen(Class("NSData"), Sel("dataWithBytes:length:"), h.AddrOfPinnedObject(), (nuint)bytes.Length); }
        finally { h.Free(); }
    }

    public static byte[]? ToBytes(nint nsData)
    {
        if (nsData == 0) return null;
        var len = (int)Send(nsData, Sel("length"));
        var ptr = Send(nsData, Sel("bytes"));
        if (len <= 0 || ptr == 0) return len == 0 ? [] : null;
        var result = new byte[len];
        Marshal.Copy(ptr, result, 0, len);
        return result;
    }

    public static List<string> ToStringList(nint nsArray)
    {
        var list = new List<string>();
        if (nsArray == 0) return list;
        var count = (long)Send(nsArray, Sel("count"));
        for (long i = 0; i < count; i++)
            if (ToStr(SendLong(nsArray, Sel("objectAtIndex:"), i)) is { } s) list.Add(s);
        return list;
    }

    /// <summary>Runs <paramref name="body"/> inside an autorelease pool so temporary objects are freed.</summary>
    public static T Pool<T>(Func<T> body)
    {
        var pool = objc_autoreleasePoolPush();
        try { return body(); }
        finally { objc_autoreleasePoolPop(pool); }
    }
}
