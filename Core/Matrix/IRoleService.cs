namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Rollen- und Berechtigungspr�fung f�r Lese-/Schreibzugriffe auf States.
    /// </summary>
    public interface IRoleService
    {
        /// <summary>
        /// Pr�ft, ob der �bergebene Benutzer den angegebenen State schreiben darf.
        /// </summary>
        bool CanWrite(string user, string stateId);

        /// <summary>
        /// Pr�ft, ob der �bergebene Benutzer den angegebenen State lesen darf.
        /// </summary>
        bool CanRead(string user, string stateId);
    }
}
