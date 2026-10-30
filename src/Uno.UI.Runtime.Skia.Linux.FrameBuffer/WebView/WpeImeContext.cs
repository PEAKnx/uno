// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Uno.UI.Runtime.Skia.Wpe;

/// <summary>
/// Managed GObject subclass of WebKitInputMethodContext (type registered at runtime, vfuncs written into the class
/// struct). WebKit calls notify_focus_in/out when an editable element starts or ends editing and notify_surrounding with
/// the text of the element; text goes back with the "committed" signal. One instance per web view.
/// </summary>
internal sealed unsafe class WpeImeContext
{
	// WebKitInputMethodContextClass = GObjectClass (size depends on GLib) + 13 pointers + 16 reserved pointers.
	// Byte offsets of the vfuncs relative to the end of GObjectClass.
	private const int RelSetEnablePreedit = 40, RelGetPreedit = 48, RelFilterKey = 56, RelFocusIn = 64, RelFocusOut = 72,
		RelCursorArea = 80, RelSurrounding = 88, RelReset = 96, TailBytes = (13 + 16) * 8;
	private const string Web = WpeNative.Web;
	private const string GObject = WpeNative.GObject;

	[StructLayout(LayoutKind.Sequential)]
	private struct GTypeQuery { public nuint Type; public IntPtr TypeName; public uint ClassSize; public uint InstanceSize; }

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ClassInit(IntPtr klass, IntPtr data);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CtxFn(IntPtr ctx);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CtxBool(IntPtr ctx, int b);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CtxArea(IntPtr ctx, int x, int y, int w, int h);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CtxSurround(IntPtr ctx, IntPtr text, uint len, uint cursor, uint selection);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CtxPreedit(IntPtr ctx, IntPtr text, IntPtr underlines, IntPtr cursor);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CtxFilter(IntPtr ctx, IntPtr keyEvent);

	[DllImport(Web)] private static extern nuint webkit_input_method_context_get_type();
	[DllImport(Web)] private static extern int webkit_input_method_context_get_input_purpose(IntPtr ctx);
	[DllImport(Web)] private static extern int webkit_input_method_context_get_input_hints(IntPtr ctx);
	[DllImport(GObject)] private static extern void g_type_query(nuint type, out GTypeQuery query);
	[DllImport(GObject)] private static extern nuint g_type_register_static_simple(nuint parent, [MarshalAs(UnmanagedType.LPStr)] string name, uint classSize,
		IntPtr classInit, uint instanceSize, IntPtr instanceInit, int flags);
	[DllImport(GObject)] private static extern IntPtr g_object_new_with_properties(nuint type, uint n, IntPtr names, IntPtr values);
	[DllImport(GObject)] private static extern void g_signal_emit_by_name(IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string signal, [MarshalAs(UnmanagedType.LPStr)] string text);

	// kept alive for the process lifetime
	private static readonly ClassInit s_classInit = OnClassInit;
	private static readonly CtxFn s_focusIn = c => Find(c)?.Raise(true);
	private static readonly CtxFn s_focusOut = c => Find(c)?.Raise(false);
	private static readonly CtxFn s_reset = c => { };
	private static readonly CtxBool s_enablePreedit = (c, b) => { };
	private static readonly CtxArea s_cursor = (c, x, y, w, h) => { };
	private static readonly CtxSurround s_surround = (c, t, l, ci, si) => Find(c)?.OnSurrounding(t == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(t, (int)l) ?? string.Empty);
	private static readonly CtxPreedit s_getPreedit = (c, t, u, o) =>
	{
		if (t != IntPtr.Zero) *(IntPtr*)t = IntPtr.Zero;
		if (u != IntPtr.Zero) *(IntPtr*)u = IntPtr.Zero;
		if (o != IntPtr.Zero) *(uint*)o = 0;
	};
	private static readonly CtxFilter s_filter = (c, k) => 0;

	private static readonly ConcurrentDictionary<IntPtr, WpeImeContext> s_instances = new();
	private static int s_base;
	private static nuint s_type;

	private readonly IntPtr _instance;

	private WpeImeContext(IntPtr instance) => _instance = instance;

	/// <summary>GLib thread: (focused, WebKitInputPurpose, WebKitInputHints).</summary>
	public event Action<bool, int, int>? FocusChanged;

	/// <summary>GLib thread: text of the editable element. Never log the value.</summary>
	public event Action<string>? SurroundingChanged;

	/// <summary>The GObject to hand to <c>webkit_web_view_set_input_method_context</c>.</summary>
	public IntPtr Handle => _instance;

	/// <summary>GLib thread. Throws if the class layout is not the one this was written for.</summary>
	public static WpeImeContext Create()
	{
		if (s_type == 0)
		{
			var parent = webkit_input_method_context_get_type();
			g_type_query(parent, out var q);
			s_base = (int)q.ClassSize - TailBytes;
			if (s_base < 120 || s_base > 160 || s_base % 8 != 0)
			{
				throw new InvalidOperationException("WebKitInputMethodContextClass layout differs from the header this was written for");
			}
			s_type = g_type_register_static_simple(parent, "PnxFrameBufferImContext", q.ClassSize, Marshal.GetFunctionPointerForDelegate(s_classInit),
				q.InstanceSize, IntPtr.Zero, 0);
		}
		var instance = g_object_new_with_properties(s_type, 0, IntPtr.Zero, IntPtr.Zero);
		var context = new WpeImeContext(instance);
		s_instances[instance] = context;
		return context;
	}

	/// <summary>Commits text into the focused element (GLib thread).</summary>
	public void Commit(string text) => g_signal_emit_by_name(_instance, "committed", text);

	/// <summary>Forgets the context (GLib thread); the GObject itself belongs to the web view.</summary>
	public void Release() => s_instances.TryRemove(_instance, out _);

	private static WpeImeContext? Find(IntPtr ctx) => s_instances.TryGetValue(ctx, out var c) ? c : null;

	private static void OnClassInit(IntPtr klass, IntPtr data)
	{
		byte* k = (byte*)klass + s_base;
		*(IntPtr*)(k + RelSetEnablePreedit) = Marshal.GetFunctionPointerForDelegate(s_enablePreedit);
		*(IntPtr*)(k + RelGetPreedit) = Marshal.GetFunctionPointerForDelegate(s_getPreedit);
		*(IntPtr*)(k + RelFilterKey) = Marshal.GetFunctionPointerForDelegate(s_filter);
		*(IntPtr*)(k + RelFocusIn) = Marshal.GetFunctionPointerForDelegate(s_focusIn);
		*(IntPtr*)(k + RelFocusOut) = Marshal.GetFunctionPointerForDelegate(s_focusOut);
		*(IntPtr*)(k + RelCursorArea) = Marshal.GetFunctionPointerForDelegate(s_cursor);
		*(IntPtr*)(k + RelSurrounding) = Marshal.GetFunctionPointerForDelegate(s_surround);
		*(IntPtr*)(k + RelReset) = Marshal.GetFunctionPointerForDelegate(s_reset);
	}

	private void OnSurrounding(string text) => SurroundingChanged?.Invoke(text);

	private void Raise(bool focused)
	{
		var purpose = focused ? webkit_input_method_context_get_input_purpose(_instance) : 0;
		var hints = focused ? webkit_input_method_context_get_input_hints(_instance) : 0;
		FocusChanged?.Invoke(focused, purpose, hints);
	}
}
