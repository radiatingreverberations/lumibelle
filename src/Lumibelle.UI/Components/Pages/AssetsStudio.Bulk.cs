using lumibelle.Models;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    [SupplyParameterFromQuery(Name = "assetBatchReview")] public Guid? RequestedAssetBatchReview { get; set; }
    private Task BulkLibraryChanged(AssetLibrary library)
    {
        if (!_dirty && !_saving && !_loading && library.ProjectId == Id) { _library = library; _savedLibrary = library.Copy(); StateHasChanged(); }
        return Task.CompletedTask;
    }
}
