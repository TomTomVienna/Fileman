namespace Fileman.Core;

/// <summary>Fehler mit einer Meldung, die direkt dem Anwender angezeigt werden kann.</summary>
public class FilemanException(string message, Exception? inner = null) : Exception(message, inner);
