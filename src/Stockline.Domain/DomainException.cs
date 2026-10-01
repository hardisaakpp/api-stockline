namespace Stockline.Domain;

public class DomainException(string message) : Exception(message);

public sealed class InvalidStateException(string message) : DomainException(message);
