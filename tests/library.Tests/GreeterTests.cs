using Xunit;

namespace library.Tests;

/// <summary>
/// Example tests for <see cref="Greeter"/>.
/// </summary>
public class GreeterTests
{
    [Fact]
    public void Greet_ReturnsGreetingWithName()
    {
        Greeter greeter = new("World");

        string result = greeter.Greet();

        Assert.Equal("Hello, World!", result);
    }

    [Theory]
    [InlineData("Alice", "Hello, Alice!")]
    [InlineData("Bob", "Hello, Bob!")]
    [InlineData("", "Hello, !")]
    public void Greet_FormatsNameIntoGreeting(string name, string expected)
    {
        Greeter greeter = new(name);

        string result = greeter.Greet();

        Assert.Equal(expected, result);
    }
}
