namespace PgrVoice;

public sealed record DialogueRegion(double Left = .18, double Top = .825, double Width = .70, double Height = .105)
{
    public bool IsValid => double.IsFinite(Left + Top + Width + Height) && Left >= 0 && Top >= 0 &&
        Width >= .08 && Height >= .025 && Left + Width <= 1.00001 && Top + Height <= 1.00001;
}
