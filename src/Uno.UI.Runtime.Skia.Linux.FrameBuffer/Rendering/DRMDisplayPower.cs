// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Uno.UI.Runtime.Skia
{
	/// <summary>
	/// Switches the display driven by the DRM renderer off or on (connector DPMS). Rendering pauses while off.
	/// </summary>
	public static class DRMDisplayPower
	{
		internal static DRMRenderer? Renderer { get; set; }

		/// <summary>True if a DRM renderer is active.</summary>
		public static bool IsAvailable => Renderer is not null;

		/// <summary>Returns false if no DRM renderer is active or the DPMS call failed.</summary>
		public static bool SetDisplayOn(bool on) => Renderer?.SetDisplayOn(on) ?? false;

		internal static long PresentedFramesCount;
		internal static long RenderTicksTotal;

		/// <summary>Frames rendered and queued for page flip since start (diagnostics).</summary>
		public static long PresentedFrames => Interlocked.Read(ref PresentedFramesCount);

		/// <summary>Stopwatch ticks spent rendering and presenting those frames (diagnostics).</summary>
		public static long RenderTicks => Interlocked.Read(ref RenderTicksTotal);
	}

	internal static class DRMDisplayPowerNative
	{
		private const string libdrm = "libdrm.so.2";

		public const ulong DpmsOn = 0;
		public const ulong DpmsOff = 3;

		[DllImport(libdrm)]
		public static extern int drmModeConnectorSetProperty(int fd, uint connectorId, uint propertyId, ulong value);

		[DllImport(libdrm)]
		private static extern IntPtr drmModeGetProperty(int fd, uint propertyId);

		[DllImport(libdrm)]
		private static extern void drmModeFreeProperty(IntPtr property);

		public static string? GetPropertyName(int fd, uint propertyId)
		{
			var property = drmModeGetProperty(fd, propertyId);
			if (property == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				// drmModePropertyRes: uint32_t prop_id, uint32_t flags, char name[32]
				return Marshal.PtrToStringAnsi(property + 8);
			}
			finally
			{
				drmModeFreeProperty(property);
			}
		}
	}
}
