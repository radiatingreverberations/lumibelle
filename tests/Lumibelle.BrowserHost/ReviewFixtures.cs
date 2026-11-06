using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Review;
using lumibelle.Services.Shots;
using Lumibelle.Testing;

internal static class ReviewFixtures
{
    public static void MapReviewFixtures(this WebApplication app)
    {
        // Three shots with prompts; the first has two takes and one selected, the second one unselected take, the third none.
        // Then a submitted plan, as an agent might have written it.
        app.MapPost("/fixtures/{id:guid}/review-plan", async (Guid id, IShotStore shots, IProductionStore production, IVideoGenerator generator, ReviewPlans plans) =>
        {
            var first = new Shot { Title = "Arrival", Duration = 2, Description = "A quiet arrival." };
            var second = new Shot { Title = "The reply", Duration = 2, Description = "She replies." };
            var third = new Shot { Title = "Not generated", Duration = 2, Description = "A future shot." };
            var document = await shots.SaveAsync(id, [first, second, third], (await shots.LoadAsync(id)).Revision);
            // Setups come before takes, as in the app: opening Shots for the first time starts them.
            foreach (var setup in (await production.InitializeAsync(id)).Compositions)
            {
                setup.Prompt = "The camera holds as she looks up.";
                await production.SaveAsync(id, setup, setup.Version);
            }
            document = await shots.LoadAsync(id);
            foreach (var (shot, candidate) in new[] { (first, 1), (first, 2), (second, 1) })
            {
                var snapshot = new VideoSnapshot(id, document.Revision, shot.Copy(), H3Policy.Compile(shot), H3Policy.Fingerprint(shot), "http://mock:8188", new(), 160, 96, H3Policy.Frames(2));
                var run = new VideoRun { Snapshot = snapshot }; var item = new VideoCandidate { Number = candidate, Seed = candidate };
                var path = Path.Combine(await shots.RunDirectoryAsync(id, run.Id), "candidate");
                var take = await ((MockVideoGenerator)generator).DownloadAsync(run, item, path, _ => Task.CompletedTask, CancellationToken.None);
                document = await shots.PublishTakeAsync(id, take, path);
            }
            document.Shots[0].SelectedTakeId = document.Takes[0].Id;
            document = await shots.SaveAsync(id, document.Shots, document.Revision);

            var plan = await plans.CreateAsync(id, "Notes on rough cut 2", "claude-code");
            var finding = await plans.AddFindingAsync(id, plan.Id, first.Id, "She holds nothing, but the next shot shows a letter.", "Prompt: “looks up”", "claude-code");
            await plans.ProposeAsync(id, plan.Id, new(ReviewStepKind.SetPrompt, first.Id, "Name the letter so it appears in the take.") { Edits = [("looks up", "looks up, holding a letter")], FindingIds = [finding.Id] }, "claude-code");
            await plans.ProposeAsync(id, plan.Id, new(ReviewStepKind.UseTake, second.Id, "The only take of the reply has not been selected.") { TakeId = document.Takes.Single(t => t.ShotId == second.Id).Id }, "claude-code");
            await plans.ProposeAsync(id, plan.Id, new(ReviewStepKind.UpdateCut, null, "Show the selected takes in the cut."), "claude-code");
            await plans.SubmitAsync(id, plan.Id, "claude-code");
            return new { plan = plan.Id };
        });
        app.MapGet("/fixtures/{id:guid}/review-state", async (Guid id, IShotStore shots, IProductionStore production, ReviewPlanStore plans) => new
        {
            prompts = (await production.LoadAsync(id)).Compositions.Select(c => c.Prompt).ToArray(),
            selected = (await shots.LoadAsync(id)).Shots.Select(s => s.SelectedTakeId).ToArray(),
            plans = (await plans.ListAsync(id)).Select(p => new { p.Id, state = p.State.ToString(), steps = p.Steps.Select(s => s.State.ToString()).ToArray() }).ToArray()
        });
    }
}
