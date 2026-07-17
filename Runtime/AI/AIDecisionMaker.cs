using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace SimpleMCPBridge.Runtime.AI
{
    /// <summary>
    /// Thin wrapper around AIRequest.AskAsync.
    /// Only handles: send request → receive response → fire onDecision.
    /// No business logic. No context collection.
    /// </summary>
    [AddComponentMenu("SimpleMCPBridge/AI Decision Maker")]
    public class AIDecisionMaker : MonoBehaviour
    {
        [Tooltip("System prompt that defines this NPC's personality and behavior rules.")]
        [TextArea(5, 20)]
        public string behaviorRules = "You are a helpful NPC in a Unity game. Respond concisely and in character.";

        /// <summary>
        /// Fired when a response arrives. Parameter: raw response text.
        /// Subscribe via Inspector drag or code: onDecision.AddListener(handler).
        /// </summary>
        public UnityEvent<string> onDecision;

        /// <summary>
        /// Send a prompt to the AI and return the response.
        /// </summary>
        /// <param name="prompt">Question or decision to make.</param>
        /// <param name="extraContext">Optional key-value context sent alongside the prompt.</param>
        /// <param name="messages">Optional conversation history array (for multi-turn dialogue).</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        public async Task<string> DecideAsync(
            string prompt,
            Dictionary<string, object> extraContext = null,
            System.Collections.IList messages = null,
            CancellationToken cancellationToken = default)
        {
            var result = await AIRequest.AskAsync(
                prompt,
                context: extraContext,
                system: behaviorRules,
                messages: messages,
                cancellationToken: cancellationToken
            );

            onDecision?.Invoke(result);
            return result;
        }

        /// <summary>Fire-and-forget. Result delivered via onDecision event.</summary>
        public void Decide(
            string prompt,
            Dictionary<string, object> extraContext = null,
            System.Collections.IList messages = null)
        {
            _ = DecideAsync(prompt, extraContext, messages);
        }
    }
}
