namespace Sakshi.Core.Ports;

/// <summary>
/// What the user sees while the system's own window picker opens. Hyprland's takes several seconds (it previews every
/// window first); without a sign that something is coming, people click around and wonder whether it worked.
/// All three are safe from any thread and in any order; <see cref="Done"/> any number of times.
/// </summary>
public interface IPickerWait
{
    /// <summary>Says the picker is on its way.</summary>
    void Opening();

    /// <summary>Says it is taking longer than it should.</summary>
    void Slow();

    /// <summary>The picker is up, or will not come: the sign goes.</summary>
    void Done();
}
