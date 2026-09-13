using System.Windows.Controls;

namespace Teezy.App;

/// <summary>Where the budget lives in Teezy: bills due, and money in and out.</summary>
/// <remarks>
/// The page exists before its data does, deliberately and visibly. Nothing reads Cashew or the
/// bills in email yet, so every card says it is empty rather than showing sample figures.
/// </remarks>
public partial class BudgetView : UserControl
{
    public BudgetView()
    {
        InitializeComponent();
    }
}
