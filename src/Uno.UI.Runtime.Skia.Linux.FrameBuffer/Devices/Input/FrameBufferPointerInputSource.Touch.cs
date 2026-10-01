// Modified by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// See the LICENSE file in the project root for more information.
//
// Base interactions with libinput derived from https://github.com/AvaloniaUI/Avalonia

#nullable enable

using System;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Input;
using Uno.UI.Runtime.Skia.Native;
using static Uno.UI.Runtime.Skia.Native.LibInput;
using static Windows.UI.Input.PointerUpdateKind;
using static Uno.UI.Runtime.Skia.Native.libinput_event_type;
using Uno.Foundation.Logging;
using System.Collections.Generic;
using Windows.Graphics.Display;
using Uno.WinUI.Runtime.Skia.Linux.FrameBuffer.UI;

namespace Uno.UI.Runtime.Skia;

unsafe internal partial class FrameBufferPointerInputSource
{
	private readonly Dictionary<uint, Point> _activePointers = new();
	// libinput reuses the slot number for every new contact (slot 0 for single touch). Uno keeps state per
	// pointer id (capture, gestures, manipulations); a stale entry of a previous contact then swallowed every
	// following touch. Like on Windows, each contact gets its own pointer id.
	private readonly Dictionary<int, uint> _slotPointerIds = new();
	private uint _nextTouchPointerId = 1;
	private readonly HashSet<libinput_event_code> _pointerPressed = new();

	public void ProcessTouchEvent(IntPtr rawEvent, libinput_event_type rawEventType)
	{
		var rawTouchEvent = libinput_event_get_touch_event(rawEvent);

		if (rawTouchEvent != IntPtr.Zero
			&& rawEventType < LIBINPUT_EVENT_TOUCH_FRAME)
		{
			var properties = new PointerPointProperties();
			var timestamp = libinput_event_touch_get_time_usec(rawTouchEvent);
			var slot = libinput_event_touch_get_slot(rawTouchEvent);
			uint pointerId;
			if (rawEventType == LIBINPUT_EVENT_TOUCH_DOWN || !_slotPointerIds.TryGetValue(slot, out pointerId))
			{
				pointerId = _nextTouchPointerId++;
				if (_nextTouchPointerId == 0)
				{
					_nextTouchPointerId = 1;
				}
				_slotPointerIds[slot] = pointerId;
			}
			if (rawEventType == LIBINPUT_EVENT_TOUCH_UP || rawEventType == LIBINPUT_EVENT_TOUCH_CANCEL)
			{
				_slotPointerIds.Remove(slot);
			}
			Action<PointerEventArgs>? raisePointerEvent = null;
			Point currentPosition;

			if (rawEventType == LIBINPUT_EVENT_TOUCH_DOWN
				|| rawEventType == LIBINPUT_EVENT_TOUCH_MOTION)
			{
				var (x, y) = GetOrientationAdjustedAbsolutionPosition(rawTouchEvent, libinput_event_touch_get_x_transformed, libinput_event_touch_get_y_transformed);
				currentPosition = new Point(x, y);
				_activePointers[pointerId] = currentPosition;
			}
			else
			{
				_activePointers.TryGetValue(pointerId, out currentPosition);
				_activePointers.Remove(pointerId);
			}

			if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().Trace($"ProcessTouchEvent: {rawEventType}, slot:{slot}, pointerId:{pointerId}, currentPosition:{currentPosition}, timestamp:{timestamp}");
			}

			switch (rawEventType)
			{
				case LIBINPUT_EVENT_TOUCH_MOTION:
					raisePointerEvent = RaisePointerMoved;
					break;

				case LIBINPUT_EVENT_TOUCH_DOWN:
					properties.PointerUpdateKind = LeftButtonPressed;
					raisePointerEvent = RaisePointerPressed;
					break;

				case LIBINPUT_EVENT_TOUCH_UP:
					properties.PointerUpdateKind = LeftButtonReleased;
					raisePointerEvent = RaisePointerReleased;
					break;

				case LIBINPUT_EVENT_TOUCH_CANCEL:
					properties.PointerUpdateKind = LeftButtonReleased;
					raisePointerEvent = RaisePointerCancelled;
					break;
			}

			properties.IsLeftButtonPressed = rawEventType != LIBINPUT_EVENT_TOUCH_UP && rawEventType != LIBINPUT_EVENT_TOUCH_CANCEL;

			var timestampInMicroseconds = timestamp;
			var pointerPoint = new Windows.UI.Input.PointerPoint(
				frameId: (uint)timestamp, // UNO TODO: How should set the frame, timestamp may overflow.
				timestamp: timestampInMicroseconds,
				device: PointerDevice.For(PointerDeviceType.Touch),
				pointerId: pointerId,
				rawPosition: currentPosition,
				position: currentPosition,
				isInContact: properties.HasPressedButton,
				properties: properties
			);

			if (raisePointerEvent != null)
			{
				var args = new PointerEventArgs(pointerPoint, GetCurrentModifiersState());

				RaisePointerEvent(raisePointerEvent, args);
			}
			else
			{
				this.Log().LogWarning($"Touch event type {rawEventType} was not handled");
			}
		}
	}
}
