using System;
using ioBroker_NewGen.Core.Matrix;

namespace ioBroker_NewGen.Core.Events
{
    /// <summary>Beschreibt eine Objektänderung für Bridge-Subscriptions.</summary>
    public sealed class MatrixObjectChangedEventArgs : EventArgs
    {
        public MatrixObjectChangedEventArgs(string objectId, MatrixObject? matrixObject)
        {
            ObjectId = objectId;
            Object = matrixObject;
        }

        public string ObjectId { get; }

        public MatrixObject? Object { get; }
    }
}
