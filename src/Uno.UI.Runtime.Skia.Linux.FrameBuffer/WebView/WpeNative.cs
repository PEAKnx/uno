// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
#nullable enable
using System;
using System.Runtime.InteropServices;

namespace Uno.UI.Runtime.Skia.Wpe;

/// <summary>
/// Managed P/Invoke surface of libwpe, wpebackend-fdo, WPE WebKit (libwpe API; WPEPlatform is not part of the
/// Buildroot build), JavaScriptCore, GLib and EGL/GLES. Nothing here is touched unless <see cref="WpeRuntime.IsInstalled"/>.
/// </summary>
internal static unsafe class WpeNative
{
	public const string Wpe = "libwpe-1.0.so.1";
	public const string Fdo = "libWPEBackend-fdo-1.0.so.1";
	public const string Web = "libWPEWebKit-2.0.so.1";
	// JavaScriptCore is part of libWPEWebKit (no separate library in the Buildroot build)
	public const string Jsc = Web;
	public const string GLib = "libglib-2.0.so.0";
	public const string GObject = "libgobject-2.0.so.0";
	public const string Egl = "libEGL.so.1";
	public const string Gles = "libGLESv2.so.2";

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ExportImage(IntPtr data, IntPtr image);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GSourceFunc(IntPtr data);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void LoadChanged(IntPtr view, int loadEvent, IntPtr data);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int LoadFailed(IntPtr view, int loadEvent, IntPtr failingUri, IntPtr error, IntPtr data);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void NotifyProperty(IntPtr instance, IntPtr pspec, IntPtr data);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void AsyncReady(IntPtr source, IntPtr result, IntPtr data);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void DestroyNotify(IntPtr data);

	[StructLayout(LayoutKind.Sequential)]
	public struct EglClient
	{
		public IntPtr ExportEglImage, ExportFdoEglImage, ExportShmBuffer, Reserved0, Reserved1;
	}

	[StructLayout(LayoutKind.Sequential)] public struct KeyboardEvent { public uint Time, KeyCode, HardwareKeyCode; [MarshalAs(UnmanagedType.U1)] public bool Pressed; public uint Modifiers; }
	[StructLayout(LayoutKind.Sequential)] public struct PointerEvent { public int Type; public uint Time; public int X, Y; public uint Button, State, Modifiers; }
	[StructLayout(LayoutKind.Sequential)] public struct AxisEvent { public int Type; public uint Time; public int X, Y; public uint Axis; public int Value; public uint Modifiers; }
	[StructLayout(LayoutKind.Sequential)] public struct TouchRaw { public int Type; public uint Time; public int Id; public int X, Y; }
	[StructLayout(LayoutKind.Sequential)] public struct TouchEvent { public TouchRaw* Points; public ulong Length; public int Type; public int Id; public uint Time; public uint Modifiers; }

	// libwpe
	[DllImport(Wpe)] public static extern int wpe_loader_init([MarshalAs(UnmanagedType.LPStr)] string lib);
	[DllImport(Wpe)] public static extern void wpe_view_backend_dispatch_set_size(IntPtr backend, uint w, uint h);
	[DllImport(Wpe)] public static extern void wpe_view_backend_dispatch_set_device_scale_factor(IntPtr backend, float scale);
	[DllImport(Wpe)] public static extern void wpe_view_backend_add_activity_state(IntPtr backend, uint state);
	[DllImport(Wpe)] public static extern void wpe_view_backend_remove_activity_state(IntPtr backend, uint state);
	[DllImport(Wpe)] public static extern void wpe_view_backend_dispatch_keyboard_event(IntPtr backend, ref KeyboardEvent e);
	[DllImport(Wpe)] public static extern void wpe_view_backend_dispatch_pointer_event(IntPtr backend, ref PointerEvent e);
	[DllImport(Wpe)] public static extern void wpe_view_backend_dispatch_axis_event(IntPtr backend, ref AxisEvent e);
	[DllImport(Wpe)] public static extern void wpe_view_backend_dispatch_touch_event(IntPtr backend, ref TouchEvent e);

	// wpebackend-fdo
	[DllImport(Fdo)] public static extern int wpe_fdo_initialize_for_egl_display(IntPtr eglDisplay);
	[DllImport(Fdo)] public static extern IntPtr wpe_view_backend_exportable_fdo_egl_create(IntPtr client, IntPtr data, uint w, uint h);
	[DllImport(Fdo)] public static extern void wpe_view_backend_exportable_fdo_destroy(IntPtr exportable);
	[DllImport(Fdo)] public static extern IntPtr wpe_view_backend_exportable_fdo_get_view_backend(IntPtr exportable);
	[DllImport(Fdo)] public static extern void wpe_view_backend_exportable_fdo_dispatch_frame_complete(IntPtr exportable);
	[DllImport(Fdo)] public static extern void wpe_view_backend_exportable_fdo_egl_dispatch_release_exported_image(IntPtr exportable, IntPtr image);
	[DllImport(Fdo)] public static extern void wpe_view_backend_exportable_fdo_egl_dispatch_release_shm_exported_buffer(IntPtr exportable, IntPtr buffer);
	[DllImport(Fdo)] public static extern IntPtr wpe_fdo_egl_exported_image_get_egl_image(IntPtr image);
	[DllImport(Fdo)] public static extern uint wpe_fdo_egl_exported_image_get_width(IntPtr image);
	[DllImport(Fdo)] public static extern uint wpe_fdo_egl_exported_image_get_height(IntPtr image);

	// WPE WebKit
	[DllImport(Web)] public static extern IntPtr webkit_web_view_backend_new(IntPtr wpeBackend, IntPtr destroy, IntPtr data);
	[DllImport(Web)] public static extern IntPtr webkit_web_view_new(IntPtr backend);
	[DllImport(Web)] public static extern void webkit_web_view_set_input_method_context(IntPtr view, IntPtr ctx);
	[DllImport(Web)] public static extern void webkit_web_view_load_uri(IntPtr view, [MarshalAs(UnmanagedType.LPStr)] string uri);
	[DllImport(Web)] public static extern void webkit_web_view_load_html(IntPtr view, [MarshalAs(UnmanagedType.LPStr)] string html, [MarshalAs(UnmanagedType.LPStr)] string? baseUri);
	[DllImport(Web)] public static extern void webkit_web_view_load_request(IntPtr view, IntPtr request);
	[DllImport(Web)] public static extern void webkit_web_view_reload(IntPtr view);
	[DllImport(Web)] public static extern void webkit_web_view_stop_loading(IntPtr view);
	[DllImport(Web)] public static extern void webkit_web_view_terminate_web_process(IntPtr view);
	[DllImport(Web)] public static extern void webkit_web_view_go_back(IntPtr view);
	[DllImport(Web)] public static extern void webkit_web_view_go_forward(IntPtr view);
	[DllImport(Web)] public static extern int webkit_web_view_can_go_back(IntPtr view);
	[DllImport(Web)] public static extern int webkit_web_view_can_go_forward(IntPtr view);
	[DllImport(Web)] public static extern IntPtr webkit_web_view_get_title(IntPtr view);
	[DllImport(Web)] public static extern IntPtr webkit_web_view_get_uri(IntPtr view);
	[DllImport(Web)] public static extern void webkit_web_view_evaluate_javascript(IntPtr view, [MarshalAs(UnmanagedType.LPStr)] string script, nint length, IntPtr world, IntPtr sourceUri, IntPtr cancellable, IntPtr callback, IntPtr data);
	[DllImport(Web)] public static extern IntPtr webkit_web_view_evaluate_javascript_finish(IntPtr view, IntPtr result, out IntPtr error);
	[DllImport(Web)] public static extern IntPtr webkit_uri_request_new([MarshalAs(UnmanagedType.LPStr)] string uri);
	[DllImport(Web)] public static extern IntPtr webkit_uri_request_get_http_headers(IntPtr request);
	[DllImport(Web)] public static extern IntPtr webkit_web_view_get_settings(IntPtr view);
	[DllImport(Web)] public static extern void webkit_settings_set_user_agent_with_application_details(IntPtr settings, [MarshalAs(UnmanagedType.LPStr)] string? name, [MarshalAs(UnmanagedType.LPStr)] string? version);
	[DllImport(Web)] public static extern void webkit_settings_set_enable_developer_extras(IntPtr settings, int enabled);
	// PEAKnx WebKit patch (BSP board/common/patches/wpewebkit): dark mode of the libwpe views
	[DllImport(Web)] public static extern void webkit_pnx_set_dark_mode(int dark);
	[DllImport(Web)] public static extern IntPtr webkit_network_session_get_default();
	[DllImport(Web)] public static extern IntPtr webkit_network_session_get_cookie_manager(IntPtr session);
	[DllImport(Web)] public static extern IntPtr webkit_network_session_get_website_data_manager(IntPtr session);
	[DllImport(Web)] public static extern IntPtr webkit_website_data_manager_get_base_data_directory(IntPtr manager);
	[DllImport(Web)] public static extern void webkit_cookie_manager_set_persistent_storage(IntPtr manager, [MarshalAs(UnmanagedType.LPUTF8Str)] string filename, int storage);

	// libsoup3 (headers of a request)
	[DllImport("libsoup-3.0.so.0")] public static extern void soup_message_headers_append(IntPtr headers, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);

	// JavaScriptCore
	[DllImport(Jsc)] public static extern IntPtr jsc_value_to_string(IntPtr value);
	[DllImport(Jsc)] public static extern IntPtr jsc_value_to_json(IntPtr value, uint indent);
	[DllImport(Jsc)] public static extern int jsc_value_is_null(IntPtr value);
	[DllImport(Jsc)] public static extern int jsc_value_is_undefined(IntPtr value);

	// GLib / GObject
	[DllImport(GObject)] public static extern ulong g_signal_connect_data(IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string signal, IntPtr cb, IntPtr data, IntPtr destroy, int flags);
	[DllImport(GObject)] public static extern void g_object_unref(IntPtr obj);
	[DllImport(GObject)] public static extern void g_signal_handler_disconnect(IntPtr instance, ulong handlerId);
	[DllImport(GLib)] public static extern IntPtr g_get_prgname();
	[DllImport(GLib)] public static extern void g_set_prgname([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
	[DllImport(GLib)] public static extern IntPtr g_main_context_default();
	[DllImport(GLib)] public static extern IntPtr g_main_loop_new(IntPtr ctx, int running);
	[DllImport(GLib)] public static extern void g_main_loop_run(IntPtr loop);
	[DllImport(GLib)] public static extern void g_main_context_invoke(IntPtr ctx, IntPtr func, IntPtr data);
	[DllImport(GLib)] public static extern void g_free(IntPtr mem);
	[DllImport(GLib)] public static extern void g_error_free(IntPtr error);

	// EGL / GLES
	[DllImport(Egl)] public static extern IntPtr eglGetCurrentDisplay();
	[DllImport(Egl)] public static extern IntPtr eglQueryString(IntPtr display, int name);
	[DllImport(Egl)] public static extern IntPtr eglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);
	[DllImport(Gles)] public static extern void glGenTextures(int n, out uint tex);
	[DllImport(Gles)] public static extern void glDeleteTextures(int n, ref uint tex);
	[DllImport(Gles)] public static extern void glBindTexture(uint target, uint tex);
	[DllImport(Gles)] public static extern void glTexParameteri(uint target, uint pname, int value);

	/// <summary>UTF-8 string owned by the callee (do not free), or null.</summary>
	public static string? Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
}
