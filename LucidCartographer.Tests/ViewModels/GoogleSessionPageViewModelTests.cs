using FluentAssertions;
using LucidCartographer.Components.Pages;
using LucidCartographer.Services.Browser;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace LucidCartographer.Tests.ViewModels;

public class GoogleSessionPageViewModelTests
{
    private readonly Mock<IBrowserSession> _session = new();

    private GoogleSessionPageViewModel CreateVm(bool remoteEnabled = false)
        => new(
            _session.Object,
            Options.Create(new BrowserOptions { RemoteView = new RemoteViewOptions { Enabled = remoteEnabled } }),
            NullLogger<GoogleSessionPageViewModel>.Instance);

    [Fact]
    public void RemoteViewEnabled_ReflectsOptions()
    {
        CreateVm(remoteEnabled: true).RemoteViewEnabled.Should().BeTrue();
        CreateVm(remoteEnabled: false).RemoteViewEnabled.Should().BeFalse();
    }

    [Fact]
    public void NoVncUrl_TargetsTheProxiedWebsockifyPath()
    {
        CreateVm().NoVncUrl.Should().StartWith("/google-session/novnc/vnc_lite.html")
            .And.Contain("path=google-session/novnc/websockify");
    }

    [Fact]
    public async Task RefreshStatus_SignedIn_SetsSignedInTrue()
    {
        _session.Setup(s => s.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleSessionStatus(SignedIn: true, Busy: false, "Signed in to Google."));

        var vm = CreateVm();
        await vm.RefreshStatusAsync();

        vm.SignedIn.Should().BeTrue();
        vm.IsBusy.Should().BeFalse();
        vm.StatusMessage.Should().Contain("Signed in");
    }

    [Fact]
    public async Task RefreshStatus_Busy_LeavesSignedInUnknown()
    {
        _session.Setup(s => s.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleSessionStatus(SignedIn: false, Busy: true, "busy"));

        var vm = CreateVm();
        await vm.RefreshStatusAsync();

        vm.SignedIn.Should().BeNull();
    }

    [Fact]
    public async Task OpenSignIn_DrivesSharedSessionToSignIn()
    {
        _session.Setup(s => s.NavigateToSignInAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleSessionStatus(SignedIn: false, Busy: false, "opened"));

        var vm = CreateVm();
        await vm.OpenSignInAsync();

        _session.Verify(s => s.NavigateToSignInAsync(It.IsAny<CancellationToken>()), Times.Once);
        vm.StatusMessage.Should().Be("opened");
    }

    [Fact]
    public async Task ResetProfile_ClearsSignedIn_AndCallsSession()
    {
        var vm = CreateVm();
        await vm.ResetProfileAsync();

        _session.Verify(s => s.ResetProfileAsync(It.IsAny<CancellationToken>()), Times.Once);
        vm.SignedIn.Should().BeFalse();
    }
}
