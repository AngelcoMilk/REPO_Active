using System;
using System.Collections.Generic;
using System.Reflection;

namespace REPO_Active.Runtime
{
    internal sealed class ExtractionPointStateTracker
    {
        private static readonly FieldInfo? CurrentStateField =
            typeof(ExtractionPoint).GetField("currentState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private readonly Dictionary<int, ExtractionPoint.State> _states = new Dictionary<int, ExtractionPoint.State>();

        public void ResetForNewRound()
        {
            _states.Clear();
        }

        public void Record(ExtractionPoint point, ExtractionPoint.State state)
        {
            if (!point)
            {
                return;
            }

            _states[point.GetInstanceID()] = state;
        }

        public ExtractionPoint.State? TryGetState(ExtractionPoint point)
        {
            if (!point)
            {
                return null;
            }

            int id = point.GetInstanceID();
            if (_states.TryGetValue(id, out var tracked))
            {
                return tracked;
            }

            var reflected = TryReadInitialState(point);
            if (reflected.HasValue)
            {
                _states[id] = reflected.Value;
            }

            return reflected;
        }

        public string ReadStateName(ExtractionPoint point)
        {
            return TryGetState(point)?.ToString() ?? "";
        }

        private static ExtractionPoint.State? TryReadInitialState(ExtractionPoint point)
        {
            if (!point || CurrentStateField == null)
            {
                return null;
            }

            try
            {
                if (CurrentStateField.GetValue(point) is ExtractionPoint.State state)
                {
                    return state;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }
    }
}
