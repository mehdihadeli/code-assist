using CodeAssist.Extensions.Skills;
using CodeAssist.Tools;
using CodeAssist.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CodeAssist.Server.Http;

internal static class SkillEndpoints
{
    internal static void MapSkills(this WebApplication app, ISkillText? skills)
    {
        app.MapGet("/v1/skills/commands", Results<Ok<IReadOnlyList<SkillCommandDto>>, ProblemHttpResult> (CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return TypedResults.Ok(skills?.Commands ?? []); }
            catch (UnauthorizedAccessException) { return TypedResults.Problem(statusCode: 403, detail: "Skill listing is restricted by policy."); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            { return TypedResults.Problem(statusCode: 422, detail: "Skill sources are not ready. Use local skill and plugin diagnostics."); }
        }).WithName("ListSkillCommands").WithTags("Skills").WithSummary("List policy-admitted human skill guidance without reading bodies or calling models.");

        app.MapPost("/v1/skills/expand", async Task<Results<Ok<SkillPromptDto>, NotFound, ProblemHttpResult>> (ExpandSkillRequest request, CancellationToken cancellationToken) =>
        {
            if (request.Id is null || !SkillListing.ValidId(request.Id) || request.Input is null || request.Input.Length > 4000 ||
                request.Arguments?.Count > 16 || request.Arguments?.Any(value => value is null || value.Length > 4000) == true)
                return TypedResults.Problem(statusCode: 400, detail: "Skill invocation data exceeds its bounds.");
            if (skills is null) return TypedResults.NotFound();
            try
            {
                var text = await skills.ExpandAsync(request.Id, request.Input, request.Arguments ?? [], cancellationToken);
                if (text is null) return TypedResults.NotFound();
                if (System.Text.Encoding.UTF8.GetByteCount(text) > 65_536) return TypedResults.Problem(statusCode: 422, detail: "Expanded skill exceeds its text budget.");
                return TypedResults.Ok(new SkillPromptDto(text));
            }
            catch (UnauthorizedAccessException) { return TypedResults.Problem(statusCode: 403, detail: "Skill invocation is restricted by policy."); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            { return TypedResults.Problem(statusCode: 422, detail: "Skill content or invocation values are invalid."); }
        }).WithName("ExpandSkillPrompt").WithTags("Skills").WithSummary("Expand explicit human guidance without gathering context, starting children or calling models.");
    }
}