namespace library;

/// <summary>
/// Generates greeting messages.
/// </summary>
public class Greeter
{
    private readonly string _name;

    /// <summary>
    /// Initializes a new instance of the <see cref="Greeter"/> class.
    /// </summary>
    /// <param name="name">The name to greet.</param>
    public Greeter(string name) => _name = name;

    /// <summary>
    /// Builds a greeting message for the configured name.
    /// </summary>
    /// <returns>A greeting string.</returns>
    public string Greet() => $"Hello, {_name}!";
}
