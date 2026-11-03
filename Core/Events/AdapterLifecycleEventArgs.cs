using System;
using ioBroker_NewGen.Core.Registry;

namespace ioBroker_NewGen.Core.Events
{
    public sealed class AdapterLifecycleEventArgs : EventArgs
    {
        public AdapterLifecycleEventArgs(AdapterRegistration registration, AdapterStatus previousStatus)
        {
            Registration = registration;
            PreviousStatus = previousStatus;
        }

        public AdapterRegistration Registration { get; }

        public AdapterStatus PreviousStatus { get; }
    }
}
