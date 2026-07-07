#if UNITY_INPUT_SYSTEM
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace SimpleMCPBridge
{

    public static class MouseDeviceTools
    {
        static Mouse virtualMouse;

        /// <summary>
        /// Get virtual mouse Device from InputSystem
        /// </summary>
        public static Mouse VirtualMouse
        {
            get
            {
                if (virtualMouse == null)
                {
                    var lastMouse = InputSystem.GetDevice<Mouse>(nameof(virtualMouse));
                    if (lastMouse != null)
                        InputSystem.RemoveDevice(lastMouse);

                    virtualMouse = InputSystem.AddDevice<Mouse>(nameof(virtualMouse));
                }
                return virtualMouse;
            }
        }
        /// <summary>
        /// Release virtual mouse device
        /// </summary>
        public static void Release()
        {
            if (virtualMouse != null)
                InputSystem.RemoveDevice(virtualMouse);
        }

        /// <summary>
        /// Trigger Click event with virtualMouse
        /// </summary>
        /// <param name="screenUV"></param>
        /// <param name="buttonId"></param>
        [Obsolete("Use ClickMouse instead. ClickVirtualMouse has bug .")]
        public static void ClickVirtualMouse(Vector2 screenUV, int buttonId = 0)
        {
            if (virtualMouse == null)
            {
                virtualMouse = InputSystem.AddDevice<Mouse>("virtualMouse");
            }

            var screenPos = new Vector2(Screen.width * screenUV.x, Screen.height * screenUV.y);
            var buttonsMask = 1u << buttonId;
            var mouseState = new MouseState
            {
                buttons = (ushort)buttonsMask,
                position = screenPos,
                clickCount = 1,
            };
            //mouseState.WithButton((MouseButton)buttonId);

            InputSystem.QueueStateEvent(Mouse.current, mouseState);
            InputSystem.Update();

            mouseState.buttons = 0;
            InputSystem.QueueStateEvent(Mouse.current, mouseState);
            InputSystem.Update();
        }
        /// <summary>
        /// Trigger Click event with virtualMouse, need focus Game window
        /// </summary>
        /// <param name="screenUV"></param>
        /// <param name="buttonId"></param>
        public static void ClickMouse(Vector2 screenUV,int buttonId=0)
        {
            var screenPos = new Vector2(Screen.width * screenUV.x, Screen.height * screenUV.y);
            var buttonsMask = 1 << buttonId; // Left mouse button
            var mouseState = new MouseState
            {
                buttons = (ushort)buttonsMask,
                position = screenPos,
                clickCount = 1,
            };
            InputSystem.QueueStateEvent(Mouse.current, mouseState);
            InputSystem.Update();

            mouseState.buttons = 0; // Release the button
            InputSystem.QueueStateEvent(Mouse.current, mouseState);
            InputSystem.Update();
        }
    }
}
#endif