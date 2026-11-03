using System;
using ioBroker_NewGen.Core.Matrix;

namespace ioBroker_NewGen.Core.Events
{
    public sealed class StateChangedEventArgs : EventArgs
    {
        public StateChangedEventArgs(string stateId, StateNode state, string? writtenBy)
        {
            StateId = stateId;
            State = state;
            WrittenBy = writtenBy;
        }

        public string StateId { get; }

        public StateNode State { get; }

        public string? WrittenBy { get; }
    }
}
