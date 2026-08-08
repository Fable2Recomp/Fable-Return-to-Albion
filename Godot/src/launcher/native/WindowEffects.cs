using System;
using System.Runtime.InteropServices;
using Godot;

namespace Refii.Launcher.Native;

public static class WindowEffects
{
	private const int WcaAccentPolicy = 19;
	private const int AccentEnableBlurBehind = 3;
	private const int DwmwaUseImmersiveDarkMode = 20;
	private const int DwmwaUseImmersiveDarkModeLegacy = 19;

	[StructLayout(LayoutKind.Sequential)]
	private struct Margins
	{
		public int LeftWidth;
		public int RightWidth;
		public int TopHeight;
		public int BottomHeight;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct AccentPolicy
	{
		public int AccentState;
		public int Flags;
		public int Color;
		public int AnimationId;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct WindowCompositionAttributeData
	{
		public int Attribute;
		public IntPtr Data;
		public IntPtr SizeOfData;
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(
		IntPtr hwnd, int attribute, ref int value, int sizeOfValue);

	[DllImport("user32.dll")]
	private static extern int SetWindowCompositionAttribute(
		IntPtr hwnd, ref WindowCompositionAttributeData data);

	public static void Apply()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		IntPtr handle;
		try
		{
			handle = (IntPtr)DisplayServer.WindowGetNativeHandle(
				DisplayServer.HandleType.WindowHandle);
		}
		catch (Exception)
		{
			return;
		}

		if (handle == IntPtr.Zero)
		{
			return;
		}

		int enabled = 1;
		try
		{
			if (DwmSetWindowAttribute(
					handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
			{
				DwmSetWindowAttribute(
					handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
			}
		}
		catch (EntryPointNotFoundException)
		{
		}

		var margins = new Margins { LeftWidth = -1 };
		try
		{
			DwmExtendFrameIntoClientArea(handle, ref margins);
		}
		catch (DllNotFoundException)
		{
			return;
		}
		catch (EntryPointNotFoundException)
		{
			return;
		}

		var policy = new AccentPolicy { AccentState = AccentEnableBlurBehind };
		IntPtr policyBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
		try
		{
			Marshal.StructureToPtr(policy, policyBuffer, false);
			var data = new WindowCompositionAttributeData
			{
				Attribute = WcaAccentPolicy,
				Data = policyBuffer,
				SizeOfData = Marshal.SizeOf<AccentPolicy>(),
			};

			SetWindowCompositionAttribute(handle, ref data);
		}
		catch (EntryPointNotFoundException)
		{
		}
		finally
		{
			Marshal.FreeHGlobal(policyBuffer);
		}
	}
}
