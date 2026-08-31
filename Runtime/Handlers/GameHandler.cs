using SimpleMCPBridge.Runtime.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// High-level game interaction tools that combine multiple low-level operations
    /// into single AI-friendly calls.
    ///
    /// Tools:
    ///   - ui.get_texts    — read all on-screen UI text (memory-based, no OCR)
    ///   - ui.find         — find interactive UI elements with screen positions
    ///   - input.action    — unified input: keys + mouse + axes + scroll
    ///   - game.get_state  — composite scene/time/UI/player perception
    ///   - game.wait       — async timing (seconds, scene load, UI appear)
    ///   - game.wait_check — poll wait completion status
    /// </summary>
    [MCPToolClass]
    public class GameHandler
    {

    }
}