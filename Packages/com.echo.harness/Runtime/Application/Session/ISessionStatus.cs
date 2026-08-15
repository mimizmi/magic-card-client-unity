namespace Echo.Harness.Application
{
    /// <summary>
    /// Read-only session state, and nothing else.
    ///
    /// <para>This exists instead of injecting <see cref="IProtocolSession"/> into a
    /// view-model. Both compile and both respect the assembly boundaries the
    /// architecture gate checks; the difference is that the wide one also hands
    /// every view-model <c>SendAsync</c> and <c>RequestAsync</c> - the ability to
    /// talk to the server without passing through a use case. The gate compares
    /// assembly reference lists and cannot see that class of bypass, so this
    /// interface is the only thing that makes the rule structural rather than
    /// advisory.</para>
    ///
    /// <para><b>There is deliberately no state-changed event.</b>
    /// <see cref="IProtocolSession"/> has none either, so an event here would have
    /// to be invented and raised by something that does not exist. Consumers poll;
    /// <c>LoginViewModel.Refresh</c> is the one that does.</para>
    /// </summary>
    public interface ISessionStatus
    {
        SessionState State { get; }
    }
}
