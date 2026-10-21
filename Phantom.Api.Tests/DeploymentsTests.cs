using Phantom.Api.Deployments;

namespace Phantom.Api.Tests;

public class DeploymentsTests
{
    [Fact]
    public void Dispatch_inputs_come_with_their_types_defaults_and_options()
    {
        var inputs = DeploymentsService.DispatchInputs("""
            name: Publish
            on:
              workflow_dispatch:
                inputs:
                  section_api:
                    description: "═══════════  API  ═══════════"
                    type: choice
                    options:
                      - "Deployed to the server"
                  deploy_api:
                    description: "Deploy the API"
                    type: boolean
                    default: true
                  version_bump:
                    description: "Version bump"
                    required: true
                    default: x.x.1
                    type: choice
                    options: [none, x.x.1, x.1.0, 1.0.0]
                  note:
                    description: "Release note"
            jobs: {}
            """)!;

        Assert.Equal(["section_api", "deploy_api", "version_bump", "note"], inputs.Select(i => i.Name));
        Assert.Equal("section", inputs[0].Type);
        Assert.Equal("API", inputs[0].Description);
        Assert.Equal(("boolean", "true"), (inputs[1].Type, inputs[1].Default));
        Assert.True(inputs[2].Required);
        Assert.Equal(["none", "x.x.1", "x.1.0", "1.0.0"], inputs[2].Options);
        Assert.Equal("string", inputs[3].Type);
    }

    [Theory]
    [InlineData("on: workflow_dispatch", true)]
    [InlineData("on: [push, workflow_dispatch]", true)]
    [InlineData("on:\n  workflow_dispatch:\n  workflow_call:", true)]
    [InlineData("on:\n  workflow_call:", false)]
    [InlineData("on: push", false)]
    public void Only_workflows_with_workflow_dispatch_can_be_run(string on, bool canRun) =>
        Assert.Equal(canRun, DeploymentsService.DispatchInputs($"name: X\n{on}\njobs: {{}}\n") is not null);

    [Fact]
    public void The_repositorys_own_workflows_parse()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "../../../../.github/workflows");
        foreach (var file in Directory.GetFiles(dir, "*.yml"))
            DeploymentsService.DispatchInputs(File.ReadAllText(file));
        var publish = DeploymentsService.DispatchInputs(File.ReadAllText(Path.Combine(dir, "Publish.yml")))!;
        Assert.Contains(publish, i => i.Name == "version_bump" && i.Type == "choice");
    }
}
