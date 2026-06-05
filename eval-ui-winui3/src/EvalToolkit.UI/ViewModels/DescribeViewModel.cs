using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using EvalToolkit.Core;
using EvalToolkit.UI.Models;

namespace EvalToolkit.UI.ViewModels;

/// <summary>
/// State for wizard step 2 — dataset description and generation
/// options. Uses MVVM Toolkit 8.4's partial-property syntax so the
/// generated PropertyChanged plumbing stays AOT-friendly under
/// CsWinRT (required for WinUI 3, per MVVMTK0045).
/// </summary>
/// <remarks>
/// <see cref="Provider"/> binds directly to <see cref="LLMProvider"/>
/// from the engine model so step 3 (slice 23) can serialize the choice
/// via <see cref="LLMProviders.ToWireString"/> without a UI-side
/// translation table. The ComboBox in <c>WizardView.xaml</c> uses
/// <see cref="ProviderOptions"/> with <c>SelectedValuePath="Value"</c>
/// to keep friendly labels separate from the wire enum.
/// </remarks>
public partial class DescribeViewModel : ObservableObject
{
    public const int DefaultCount = 30;
    public const int MinCount = 10;
    public const int MaxCount = 50;

    public const string DefaultExtensions =
        "csv,json,jsonl,xlsx,xls,tsv,docx,pdf,pptx,txt,md";

    public static readonly IReadOnlyList<ProviderOption> ProviderOptions = new[]
    {
        new ProviderOption("Microsoft 365 Copilot (WorkIQ MCP)", LLMProvider.M365Copilot),
        new ProviderOption("Microsoft 365 Copilot API", LLMProvider.M365CopilotApi),
        new ProviderOption("WorkIQ A2A", LLMProvider.WorkIqA2a),
        new ProviderOption("Azure OpenAI", LLMProvider.AzureOpenAi),
        new ProviderOption("GitHub Copilot CLI", LLMProvider.GitHubCopilot),
        new ProviderOption("Custom command", LLMProvider.Command),
    };

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial string Extensions { get; set; }

    [ObservableProperty]
    public partial LLMProvider Provider { get; set; }

    [ObservableProperty]
    public partial string Model { get; set; }

    [ObservableProperty]
    public partial string M365TenantId { get; set; }

    [ObservableProperty]
    public partial string ConnectorSchemaPath { get; set; }

    public DescribeViewModel()
    {
        Description = string.Empty;
        Count = DefaultCount;
        Extensions = DefaultExtensions;
        Provider = LLMProvider.M365Copilot;
        Model = string.Empty;
        M365TenantId = string.Empty;
        ConnectorSchemaPath = string.Empty;
    }

    /// <summary>
    /// Whether the description field has user-entered content.
    /// </summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>
    /// Whether <see cref="Count"/> sits inside the legacy 10..50 band.
    /// </summary>
    public bool CountInRange => Count >= MinCount && Count <= MaxCount;

    partial void OnDescriptionChanged(string value)
    {
        OnPropertyChanged(nameof(HasDescription));
    }

    partial void OnCountChanged(int value)
    {
        if (value < MinCount)
        {
            Count = MinCount;
            return;
        }
        if (value > MaxCount)
        {
            Count = MaxCount;
            return;
        }
        OnPropertyChanged(nameof(CountInRange));
    }
}
