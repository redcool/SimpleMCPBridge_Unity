using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace SimpleMCPBridge.Runtime.AI
{
    /// <summary>
    /// Multi-turn dialogue for NPCs. Composes AIDecisionMaker for AI calls.
    /// Only manages conversation history. The AI response arrives via onNpcResponse.
    /// </summary>
    [AddComponentMenu("SimpleMCPBridge/AI Dialogue")]
    public class AIDialogue : MonoBehaviour
    {
        /// <summary>AIDecisionMaker that sends/receives AI requests. Drag in Inspector.</summary>
        public AIDecisionMaker decisionMaker;

        [Tooltip("Name of this NPC as shown to the AI.")]
        public string npcName = "NPC";

        [Tooltip("Maximum conversation turns kept in history. Older entries are dropped.")]
        public int maxHistoryLength = 20;

        /// <summary>Fired when the NPC responds. Parameter: response text.</summary>
        public UnityEvent<string> onNpcResponse;

        private readonly List<Dictionary<string, object>> _history = new();

        /// <summary>
        /// Send player input and get the NPC's response.
        /// Automatically manages conversation history.
        /// </summary>
        /// <param name="playerMessage">What the player said.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>NPC's response text.</returns>
        public async Task<string> TalkAsync(
            string playerMessage,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(playerMessage)) return string.Empty;

            _history.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", playerMessage }
            });
            TrimHistory();

            if(!decisionMaker)
                decisionMaker = GetComponent<AIDecisionMaker>();

            if(!decisionMaker)
            {
                const string errorMessage = "No AIDecisionMaker found.";
                Debug.LogError(errorMessage);
                onNpcResponse?.Invoke(errorMessage);
                return errorMessage;
            }

            var result = await decisionMaker.DecideAsync(
                playerMessage,
                messages: _history,
                cancellationToken: cancellationToken
            );

            if (!string.IsNullOrEmpty(result))
            {
                _history.Add(new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", result }
                });
                TrimHistory();
            }

            onNpcResponse?.Invoke(result);
            return result;
        }

        /// <summary>Fire-and-forget talk. Response via onNpcResponse.</summary>
        public void Talk(string playerMessage)
        {
            _ = TalkAsync(playerMessage);
        }

        /// <summary>Reset conversation history.</summary>
        public void ClearHistory()
        {
            _history.Clear();
        }

        private void TrimHistory()
        {
            while (_history.Count > maxHistoryLength * 2)
                _history.RemoveAt(0);
        }
    }
}
