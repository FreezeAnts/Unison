namespace Unison.Windows;

public readonly record struct HostRect(int Left, int Top, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}
