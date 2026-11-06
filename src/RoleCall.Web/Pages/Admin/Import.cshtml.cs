using System.Text;
using Microsoft.AspNetCore.Mvc;
using RoleCall.Domain.Model;
using RoleCall.Web.Services;

namespace RoleCall.Web.Pages.Admin;

public sealed class ImportModel(ReviewService reviews) : AppPage
{
    public Policy Policy { get; private set; } = new();
    public Overview Data { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Policy = await reviews.PolicyAsync();
        Data = await reviews.OverviewAsync(Actor);
    }

    public Task<IActionResult> OnPostPeopleAsync(IFormFile? file) =>
        Run(async () => $"HR feed imported: {await reviews.ImportPeopleAsync(Actor, file?.FileName ?? "", await Read(file))} people.");

    public Task<IActionResult> OnPostAccountsAsync(string app, IFormFile? file) =>
        Run(async () => $"{await reviews.ImportAccountsAsync(Actor, app ?? "", file?.FileName ?? "", await Read(file))} accounts of {app} imported. They replace the previous export of {app}.");

    public Task<IActionResult> OnPostPolicyAsync(IFormFile? file) =>
        Run(async () =>
        {
            await reviews.ImportPolicyAsync(Actor, file?.FileName ?? "", await Read(file));
            return "Policy updated. Findings use it now; an open campaign keeps the policy it was started with.";
        });

    static async Task<string> Read(IFormFile? file)
    {
        if (file is null || file.Length == 0) throw new AccessException(AccessError.Invalid, "Choose a file.");
        if (file.Length > 10 * 1024 * 1024) throw new AccessException(AccessError.Invalid, "The file is larger than 10 MB.");
        using var reader = new StreamReader(file.OpenReadStream(), new UTF8Encoding(false, throwOnInvalidBytes: true));
        try { return await reader.ReadToEndAsync(); }
        catch (DecoderFallbackException) { throw new AccessException(AccessError.Invalid, "The file is not UTF-8 text. Save it as \"CSV UTF-8\"."); }
    }
}
