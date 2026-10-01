using System;
using System.Runtime.InteropServices;

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
