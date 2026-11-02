using GapWatch.Core.Model;

namespace GapWatch.Core;

/// <summary>Turns a serialized API description into the normalized <see cref="ApiContract"/>.</summary>
public interface IContractReader
{
    /// <summary>True when this reader understands the file, judged by its name or extension.</summary>
    bool CanRead(string fileName);

    /// <summary>Reads and normalizes the contract. Throws <see cref="ContractReadException"/> on invalid input.</summary>
    ApiContract Read(Stream stream);
}

/// <summary>The input could not be read as a supported API description.</summary>
public sealed class ContractReadException : Exception
{
    public ContractReadException(string message) : base(message)
    {
    }

    public ContractReadException(string message, Exception inner) : base(message, inner)
    {
    }
}
