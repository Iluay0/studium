namespace Studium.Core;

public static class NameFormatter
{
    /// <summary>
    /// Full: "Iluay Dory". Initials: "I. D.". SurnameInitial: "Iluay D.".
    /// With <paramref name="youForSelf"/>, your own name is "YOU" whatever the style.
    /// Single-word names (pets, NPCs) are never shortened.
    /// </summary>
    public static string Format(string name, bool isSelf, NameDisplay display, bool youForSelf)
    {
        if (youForSelf && isSelf)
            return "YOU";
        if (display == NameDisplay.Full)
            return name;

        var space = name.IndexOf(' ');
        if (space <= 0 || space == name.Length - 1)
            return name;

        var first = name[..space];
        var surnameInitial = name[space + 1];
        return display == NameDisplay.Initials ? $"{first[0]}. {surnameInitial}." : $"{first} {surnameInitial}.";
    }
}
