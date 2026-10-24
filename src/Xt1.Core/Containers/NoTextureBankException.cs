namespace Xt1.Core.Containers;

/// <summary>
/// A container that parsed cleanly but holds no texture bank: neither a "wta" sub-file nor an inline
/// "wtb" one (the effect folder is full of "epb" containers, and a payload-side file can be missing
/// its header-side partner). Callers that scan a whole directory classify these as "not a texture
/// bank" rather than a failure, so this is a type of its own instead of a message they have to match.
/// </summary>
/// <remarks>
/// Deliberately not derived from <see cref="InvalidDataException"/>, which is sealed: the file itself
/// is well formed, there is just nothing here to edit.
/// </remarks>
public sealed class NoTextureBankException : Exception
{
    public NoTextureBankException(string message) : base(message)
    {
    }
}
