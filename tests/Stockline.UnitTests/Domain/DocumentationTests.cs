using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Domain;

/// <summary>Keeps the diagram in the README honest: a transition added to the state machine must be documented.</summary>
public class DocumentationTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stockline.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [Fact]
    public void Readme_diagram_lists_every_transition_of_the_state_machine()
    {
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));

        foreach (var (from, action, to) in OrderStateMachine.Transitions)
        {
            Assert.Contains($"{from} --> {to}: {action}", readme);
        }
    }

    [Fact]
    public void Readme_diagram_has_no_transition_the_state_machine_lacks()
    {
        var readme = File.ReadAllLines(Path.Combine(RepositoryRoot(), "README.md"));
        var known = OrderStateMachine.Transitions.Select(t => $"{t.From} --> {t.To}: {t.Action}").ToHashSet();

        var documented = readme
            .Select(l => l.Trim())
            .Where(l => l.Contains(" --> ") && l.Contains(": ") && !l.StartsWith("[*]"))
            .ToList();

        Assert.NotEmpty(documented);
        Assert.All(documented, line => Assert.Contains(line, known));
    }
}
