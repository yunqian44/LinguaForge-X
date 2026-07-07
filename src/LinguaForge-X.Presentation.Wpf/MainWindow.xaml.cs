using LinguaForge_X.Application.UseCases;
using System.Windows;

namespace LinguaForge_X.Presentation.Wpf;

public partial class MainWindow : Window
{
    public MainWindow(GetGreetingUseCase getGreetingUseCase)
    {
        InitializeComponent();
        GreetingText.Text = getGreetingUseCase.Execute().Message;
    }
}