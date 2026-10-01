// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Helpers;

namespace Uno.UI.Runtime.Skia;

/// <summary>
/// GPU interop for apps on the DRM (OpenGL ES) renderer: work on the render thread with the GL context current, and
/// DMA-BUF images (e.g. hardware decoded video frames) imported as Skia images without copying them.
/// </summary>
public static class FrameBufferGpu
{
	private static readonly ConcurrentQueue<Action<GRContext>> s_pending = new();
	private static DRMRenderer? s_renderer;
	private static int s_renderThreadId;

	/// <summary>True while the DRM renderer renders (false with the software renderer).</summary>
	public static bool IsAvailable => s_renderer is not null;

	/// <summary>
	/// Runs <paramref name="action"/> on the render thread before the next frame, with the GL context current; Skia's
	/// cached GL state is reset afterwards. Any thread. False if there is no GPU renderer.
	/// </summary>
	public static bool TryInvoke(Action<GRContext> action)
	{
		var renderer = s_renderer;
		if (renderer is null)
		{
			return false;
		}
		s_pending.Enqueue(action);
		renderer.InvalidateRender();
		return true;
	}

	/// <summary>
	/// Render thread (inside <see cref="TryInvoke"/>): imports a DMA-BUF image as a texture-backed image without copying
	/// it. YUV formats (e.g. NV12, also with tiled or Broadcom SAND modifiers) are sampled as RGB through an external
	/// texture; the driver converts them with the BT.709 or BT.601 matrix and the given range. Single-plane formats
	/// (R8, GR88, ABGR8888) become 2D textures. The file descriptors stay owned by the caller and can be closed after
	/// the call. The texture and EGL image are released with the image, which should be disposed on the render thread.
	/// Returns null if the import failed (logged), also for a non-linear modifier without
	/// EGL_EXT_image_dma_buf_import_modifiers.
	/// </summary>
	public static SKImage? ImportDmaBuf(GRContext context, int width, int height, uint fourcc, int[] fds, int[] offsets,
		int[] pitches, ulong modifier, bool bt709, bool fullRange)
	{
		if (Environment.CurrentManagedThreadId != s_renderThreadId)
		{
			throw new InvalidOperationException($"{nameof(ImportDmaBuf)} must run on the render thread ({nameof(TryInvoke)}).");
		}
		var planes = Math.Min(fds.Length, Math.Min(offsets.Length, pitches.Length));
		if (planes is < 1 or > 3)
		{
			throw new ArgumentException("1 to 3 planes expected.", nameof(fds));
		}
		if (!Gl.TryLoad(out var error))
		{
			LogOnce($"DMA-BUF import not available: {error}");
			return null;
		}
		if (!Gl.Modifiers && modifier is not (DrmFormatModLinear or DrmFormatModInvalid))
		{
			// Without the modifier attributes a tiled buffer would be read as linear
			LogOnce($"DMA-BUF modifier 0x{modifier:x} not importable (no EGL_EXT_image_dma_buf_import_modifiers)");
			return null;
		}

		var (colorType, glFormat) = fourcc switch
		{
			DrmFourccR8 => (SKColorType.R8Unorm, GlR8),
			DrmFourccGr88 => (SKColorType.Rg88, GlRg8),
			DrmFourccAbgr8888 => (SKColorType.Rgba8888, GlRgba8),
			_ => (SKColorType.Unknown, 0u),
		};
		// Anything else is YUV: sampled through an external texture, converted by the driver
		var external = colorType == SKColorType.Unknown;
		var target = external ? GlTextureExternalOes : GlTexture2D;

		var attribs = new List<int> { EglWidth, width, EglHeight, height, EglLinuxDrmFourcc, (int)fourcc };
		for (var i = 0; i < planes; i++)
		{
			attribs.AddRange([EglDmaBufPlane0Fd + 3 * i, fds[i], EglDmaBufPlane0Offset + 3 * i, offsets[i], EglDmaBufPlane0Pitch + 3 * i, pitches[i]]);
			if (Gl.Modifiers && modifier != DrmFormatModInvalid)
			{
				attribs.AddRange([EglDmaBufPlane0ModifierLo + 2 * i, (int)(modifier & 0xffffffff), EglDmaBufPlane0ModifierHi + 2 * i, (int)(modifier >> 32)]);
			}
		}
		if (external)
		{
			attribs.AddRange([EglYuvColorSpaceHint, bt709 ? EglItuRec709 : EglItuRec601, EglSampleRangeHint, fullRange ? EglYuvFullRange : EglYuvNarrowRange]);
		}
		attribs.Add(EglNone);

		var display = EglHelper.EglGetCurrentDisplay();
		var eglImage = Gl.CreateImage(display, attribs.ToArray());
		if (eglImage == IntPtr.Zero)
		{
			LogOnce($"eglCreateImageKHR failed ({EglHelper.EglGetError()}, fourcc 0x{fourcc:x}, modifier 0x{modifier:x}, {planes} planes)");
			return null;
		}

		var texture = Gl.CreateTexture(target, eglImage, out var glError);
		if (texture == 0)
		{
			Gl.DestroyImage(display, eglImage);
			LogOnce($"glEGLImageTargetTexture2DOES failed (0x{glError:x}, fourcc 0x{fourcc:x}, modifier 0x{modifier:x})");
			return null;
		}
		context.ResetContext(); // texture binding changed behind Skia

		var resources = new TextureResources(display, eglImage, texture);
		using var backend = new GRBackendTexture(width, height, false, new GRGlTextureInfo(target, texture, external ? GlRgba8 : glFormat));
		var image = SKImage.FromTexture(context, backend, GRSurfaceOrigin.TopLeft, external ? SKColorType.Rgba8888 : colorType,
			SKAlphaType.Opaque, null, static state => ((TextureResources)state!).Release(), resources);
		if (image is null)
		{
			resources.Release();
			LogOnce($"SKImage.FromTexture failed (fourcc 0x{fourcc:x}, external {external})");
		}
		return image;
	}

	internal static void Attach(DRMRenderer renderer) => s_renderer = renderer;

	internal static void Detach(DRMRenderer renderer)
	{
		if (ReferenceEquals(s_renderer, renderer))
		{
			s_renderer = null;
		}
	}

	/// <summary>Render thread, before each frame.</summary>
	internal static void RunPending(GRContext context)
	{
		s_renderThreadId = Environment.CurrentManagedThreadId;
		if (s_pending.IsEmpty)
		{
			return;
		}
		while (s_pending.TryDequeue(out var action))
		{
			try
			{
				action(context);
			}
			catch (Exception e)
			{
				Log(LogLevel.Error, "A render thread action failed.", e);
			}
		}
		context.ResetContext();
	}

	private static void Log(LogLevel level, string message, Exception? e = null)
	{
		var log = typeof(FrameBufferGpu).Log();
		if (!log.IsEnabled(level))
		{
			return;
		}
		switch (level)
		{
			case LogLevel.Error:
				log.Error(message, e);
				break;
			case LogLevel.Warning:
				log.Warn(message);
				break;
			default:
				log.Info(message);
				break;
		}
	}

	private static int s_logged;

	private static void LogOnce(string message)
	{
		if (Interlocked.Exchange(ref s_logged, 1) == 0)
		{
			Log(LogLevel.Warning, message);
		}
	}

	/// <summary>GL texture and EGL image of an imported image, released on the render thread.</summary>
	private sealed class TextureResources(IntPtr display, IntPtr eglImage, uint texture)
	{
		private int _released;

		public void Release()
		{
			if (Interlocked.Exchange(ref _released, 1) != 0)
			{
				return;
			}
			if (Environment.CurrentManagedThreadId == s_renderThreadId)
			{
				Free(null);
			}
			else if (!TryInvoke(Free))
			{
				Log(LogLevel.Warning, "Imported texture released without a renderer; not freed.");
			}
		}

		private void Free(GRContext? _)
		{
			Gl.DeleteTexture(texture);
			Gl.DestroyImage(display, eglImage);
		}
	}

	private static unsafe class Gl
	{
		private static bool s_loaded;
		private static string? s_error;
		private static delegate* unmanaged<IntPtr, int, IntPtr> s_eglQueryString;
		private static delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, int*, IntPtr> s_eglCreateImage;
		private static delegate* unmanaged<IntPtr, IntPtr, uint> s_eglDestroyImage;
		private static delegate* unmanaged<uint, IntPtr, void> s_imageTargetTexture;
		private static delegate* unmanaged<int, uint*, void> s_genTextures;
		private static delegate* unmanaged<int, uint*, void> s_deleteTextures;
		private static delegate* unmanaged<uint, uint, void> s_bindTexture;
		private static delegate* unmanaged<uint, uint, int, void> s_texParameteri;
		private static delegate* unmanaged<uint> s_getError;

		public static bool Modifiers { get; private set; }

		public static bool TryLoad(out string? error)
		{
			if (!s_loaded && s_error is null)
			{
				try
				{
					s_eglQueryString = (delegate* unmanaged<IntPtr, int, IntPtr>)Proc("eglQueryString");
					var extensions = Marshal.PtrToStringAnsi(s_eglQueryString(EglHelper.EglGetCurrentDisplay(), EglExtensions)) ?? "";
					if (!extensions.Contains("EGL_EXT_image_dma_buf_import"))
					{
						throw new NotSupportedException("EGL_EXT_image_dma_buf_import missing");
					}
					Modifiers = extensions.Contains("EGL_EXT_image_dma_buf_import_modifiers");
					s_eglCreateImage = (delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, int*, IntPtr>)Proc("eglCreateImageKHR");
					s_eglDestroyImage = (delegate* unmanaged<IntPtr, IntPtr, uint>)Proc("eglDestroyImageKHR");
					s_imageTargetTexture = (delegate* unmanaged<uint, IntPtr, void>)Proc("glEGLImageTargetTexture2DOES");
					s_genTextures = (delegate* unmanaged<int, uint*, void>)Proc("glGenTextures");
					s_deleteTextures = (delegate* unmanaged<int, uint*, void>)Proc("glDeleteTextures");
					s_bindTexture = (delegate* unmanaged<uint, uint, void>)Proc("glBindTexture");
					s_texParameteri = (delegate* unmanaged<uint, uint, int, void>)Proc("glTexParameteri");
					s_getError = (delegate* unmanaged<uint>)Proc("glGetError");
					s_loaded = true;
					Log(LogLevel.Information, $"DMA-BUF import available (modifiers: {Modifiers}).");
				}
				catch (NotSupportedException e)
				{
					s_error = e.Message;
				}
			}
			error = s_error;
			return s_loaded;
		}

		public static IntPtr CreateImage(IntPtr display, int[] attribs)
		{
			fixed (int* a = attribs)
			{
				return s_eglCreateImage(display, IntPtr.Zero, EglLinuxDmaBuf, IntPtr.Zero, a);
			}
		}

		public static void DestroyImage(IntPtr display, IntPtr image) => s_eglDestroyImage(display, image);

		public static uint CreateTexture(uint target, IntPtr image, out uint error)
		{
			uint texture;
			s_genTextures(1, &texture);
			s_bindTexture(target, texture);
			s_texParameteri(target, GlTextureMinFilter, GlLinear);
			s_texParameteri(target, GlTextureMagFilter, GlLinear);
			s_texParameteri(target, GlTextureWrapS, GlClampToEdge);
			s_texParameteri(target, GlTextureWrapT, GlClampToEdge);
			s_imageTargetTexture(target, image);
			error = s_getError();
			s_bindTexture(target, 0);
			if (error != 0)
			{
				s_deleteTextures(1, &texture);
				return 0;
			}
			return texture;
		}

		public static void DeleteTexture(uint texture) => s_deleteTextures(1, &texture);

		private static IntPtr Proc(string name)
		{
			var proc = EglHelper.EglGetProcAddress(name);
			return proc != IntPtr.Zero ? proc : throw new NotSupportedException($"{name} missing");
		}
	}

	private const uint DrmFourccR8 = 0x20203852, DrmFourccGr88 = 0x38385247, DrmFourccAbgr8888 = 0x34324241;
	private const ulong DrmFormatModLinear = 0, DrmFormatModInvalid = 0x00ffffffffffffff;
	private const int EglExtensions = 0x3055, EglWidth = 0x3057, EglHeight = 0x3056, EglNone = 0x3038;
	private const uint EglLinuxDmaBuf = 0x3270;
	private const int EglLinuxDrmFourcc = 0x3271, EglDmaBufPlane0Fd = 0x3272, EglDmaBufPlane0Offset = 0x3273, EglDmaBufPlane0Pitch = 0x3274,
		EglDmaBufPlane0ModifierLo = 0x3443, EglDmaBufPlane0ModifierHi = 0x3444;
	private const int EglYuvColorSpaceHint = 0x327B, EglSampleRangeHint = 0x327C, EglItuRec601 = 0x327F, EglItuRec709 = 0x3280,
		EglYuvFullRange = 0x3282, EglYuvNarrowRange = 0x3283;
	private const uint GlTexture2D = 0x0DE1, GlTextureExternalOes = 0x8D65, GlTextureMinFilter = 0x2801, GlTextureMagFilter = 0x2800,
		GlTextureWrapS = 0x2802, GlTextureWrapT = 0x2803, GlR8 = 0x8229, GlRg8 = 0x822B, GlRgba8 = 0x8058;
	private const int GlLinear = 0x2601, GlClampToEdge = 0x812F;
}
