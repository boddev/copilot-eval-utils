using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        "csv,json,jsonl,xlsx,tsv,docx,pdf,pptx,txt,md";

    /// <summary>
    /// Sentinel shown in the (disabled) model field for providers that
    /// manage model selection themselves (M365 Copilot / WorkIQ). It is
    /// never sent to the engine — see <see cref="EffectiveModel"/>.
    /// </summary>
    public const string AutoModel = "Auto";

    public static readonly IReadOnlyList<ProviderOption> ProviderOptions = new[]
    {
        new ProviderOption("Microsoft 365 Copilot (WorkIQ MCP)", LLMProvider.M365Copilot),
        new ProviderOption("Microsoft 365 Copilot API", LLMProvider.M365CopilotApi),
        new ProviderOption("WorkIQ A2A", LLMProvider.WorkIqA2a),
        new ProviderOption("Azure OpenAI", LLMProvider.AzureOpenAi),
        new ProviderOption("GitHub Copilot CLI", LLMProvider.GitHubCopilot),
        // LLMProvider.Command intentionally omitted in slice 23 — there is no UI
        // field yet to capture --llm-command, and selecting it without setting
        // EVALGEN_LLM_COMMAND silently fails at client construction. Re-add when
        // the advanced-options panel grows a "custom command" field.
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
        // Default provider (M365 Copilot) manages model selection itself.
        Model = AutoModel;
        M365TenantId = string.Empty;
        ConnectorSchemaPath = string.Empty;
        AvailableModels = new ObservableCollection<string>(GitHubCopilotModelCatalog.KnownModels);
    }

    /// <summary>
    /// Candidate model identifiers shown in the editable model ComboBox
    /// when <see cref="LLMProvider.GitHubCopilot"/> is selected. These are
    /// the GitHub Copilot CLI model slugs; the ComboBox stays editable so an
    /// operator can type a model the list does not include.
    /// </summary>
    public ObservableCollection<string> AvailableModels { get; }

    /// <summary>GitHub Copilot CLI — model is picked from <see cref="AvailableModels"/>.</summary>
    public bool IsModelDropdown => Provider == LLMProvider.GitHubCopilot;

    /// <summary>Azure OpenAI — model/deployment name is free text.</summary>
    public bool IsModelTextBox => Provider == LLMProvider.AzureOpenAi;

    /// <summary>M365 Copilot / WorkIQ — model is managed by the provider (Auto).</summary>
    public bool IsModelAuto =>
        Provider is LLMProvider.M365Copilot
                 or LLMProvider.M365CopilotApi
                 or LLMProvider.WorkIqA2a;

    /// <summary>
    /// Model value actually handed to the engine. The <see cref="AutoModel"/>
    /// sentinel is provider-scoped (only meaningful while
    /// <see cref="IsModelAuto"/> is true) and is collapsed to empty so it
    /// never leaks into job metadata or a provider's <c>--model</c> arg.
    /// </summary>
    public string EffectiveModel => IsModelAuto ? string.Empty : Model;

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

    partial void OnProviderChanged(LLMProvider value)
    {
        // Keep the model field's mode + value consistent with the chosen
        // provider. Switching INTO an Auto provider parks the field on the
        // sentinel; switching OUT of it (to a provider that takes a real
        // model) clears the sentinel so the user starts from a blank box
        // rather than a literal "Auto" model id.
        if (IsModelAuto)
        {
            Model = AutoModel;
        }
        else if (string.Equals(Model, AutoModel, System.StringComparison.Ordinal))
        {
            Model = string.Empty;
        }

        OnPropertyChanged(nameof(IsModelDropdown));
        OnPropertyChanged(nameof(IsModelTextBox));
        OnPropertyChanged(nameof(IsModelAuto));
        OnPropertyChanged(nameof(EffectiveModel));
    }

    partial void OnModelChanged(string value)
    {
        OnPropertyChanged(nameof(EffectiveModel));
    }
}
