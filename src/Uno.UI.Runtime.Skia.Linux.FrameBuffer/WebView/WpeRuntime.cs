// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Uno.Foundation.Logging;

namespace Uno.UI.Runtime.Skia.Wpe;

/// <summary>
/// Process-wide WPE state: engine detection, the GLib thread (WebKit's main run loop lives on the default GLib main
/// context, which this thread owns and runs) and the one-time wpebackend-fdo initialization with the host's EGL display.
/// </summary>
internal static class WpeRuntime
{
	private static readonly object s_gate = new();
	private static readonly Queue<Action> s_early = new();
	private static readonly WpeNative.GSourceFunc s_trampoline = RunQueued;
	private static bool? s_installed;
	private static bool s_starting;
	private static volatile bool s_ready;
	private static IntPtr s_context;
	private static string? s_failure;

	/// <summary>True if libWPEWebKit, libWPEBackend-fdo and libwpe load. Checked once; never creates anything.</summary>
	public static bool IsInstalled
	{
		get
		{
			lock (s_gate)
			{
				if (s_installed is null)
				{
					var missing = new List<string>();
					foreach (var lib in new[] { WpeNative.Web, WpeNative.Fdo, WpeNative.Wpe })
					{
						if (NativeLibrary.TryLoad(lib, out var handle))
						{
							NativeLibrary.Free(handle);
						}
						else
						{
							missing.Add(lib);
						}
					}
					s_installed = missing.Count == 0;
					Log(s_installed.Value ? "WPE WebKit found" : "WPE WebKit not available, missing " + string.Join(", ", missing));
				}
				return s_installed.Value;
			}
		}
	}

	/// <summary>Why the runtime failed to start (null while it is running or not started).</summary>
	public static string? Failure => s_failure;

	/// <summary>
	/// Runs <paramref name="action"/> on the GLib thread, as soon as the runtime is up. Starts the runtime on the first call:
	/// the EGL display is taken on the host's render thread.
	/// </summary>
	public static void Run(Action action)
	{
		bool start = false;
		lock (s_gate)
		{
			if (s_failure is not null)
			{
				return;
			}
			if (!s_ready)
			{
				s_early.Enqueue(action);
				start = !s_starting;
				s_starting = true;
			}
		}
		if (s_ready)
		{
			Invoke(action);
		}
		else if (start && !FrameBufferGpu.TryInvoke(_ => Start(WpeNative.eglGetCurrentDisplay())))
		{
			Fail("the host has no GPU renderer");
		}
	}

	private static void Start(IntPtr eglDisplay)
	{
		var thread = new Thread(() =>
		{
			try
			{
				if (WpeNative.wpe_loader_init(WpeNative.Fdo) == 0)
				{
					Fail("wpe_loader_init failed");
					return;
				}
				if (WpeNative.wpe_fdo_initialize_for_egl_display(eglDisplay) == 0)
				{
					Fail("wpe_fdo_initialize_for_egl_display failed");
					return;
				}
				s_context = WpeNative.g_main_context_default();
				List<Action> early;
				lock (s_gate)
				{
					early = new List<Action>(s_early);
					s_early.Clear();
					s_ready = true;
				}
				// this thread is the GLib thread: run what was waiting for the runtime, then serve the loop
				foreach (var a in early)
				{
					RunGuarded(a);
				}
				var loop = WpeNative.g_main_loop_new(s_context, 0);
				WpeNative.g_main_loop_run(loop);
			}
			catch (Exception e)
			{
				Fail(e.GetType().Name + ": " + e.Message);
			}
		})
		{ IsBackground = true, Name = "WPE GLib" };
		thread.Start();
	}

	/// <summary>Runs <paramref name="action"/> on the GLib thread. The runtime must be ready (any thread).</summary>
	public static void Invoke(Action action)
	{
		var handle = GCHandle.Alloc(action);
		WpeNative.g_main_context_invoke(s_context, Marshal.GetFunctionPointerForDelegate(s_trampoline), GCHandle.ToIntPtr(handle));
	}

	private static int RunQueued(IntPtr data)
	{
		var handle = GCHandle.FromIntPtr(data);
		var action = (Action)handle.Target!;
		handle.Free();
		RunGuarded(action);
		return 0;
	}

	private static void RunGuarded(Action action)
	{
		try
		{
			action();
		}
		catch (Exception e)
		{
			// an exception must not end the GLib thread: every WPE view would stop working
			Log("exception on the GLib thread: " + e);
		}
	}

	private static void Fail(string reason)
	{
		lock (s_gate)
		{
			s_failure = reason;
			s_early.Clear();
		}
		Log("WPE runtime failed: " + reason);
	}

	internal static void Log(string message)
	{
		if (typeof(WpeRuntime).Log().IsEnabled(LogLevel.Information))
		{
			typeof(WpeRuntime).Log().Info("[wpe] " + message);
		}
	}
}
