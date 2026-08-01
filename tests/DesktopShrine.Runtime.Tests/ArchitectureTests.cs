using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Media;
using DesktopShrine.Contracts.Audio;
using DesktopShrine.Runtime;
using Xunit;
namespace DesktopShrine.Runtime.Tests;
public sealed class ArchitectureTests
{
    [Fact] public void ValidContractPackageRegisters() { var r = new ContractRegistry(); r.RegisterAssembly(typeof(NowPlayingState).Assembly); r.RegisterAssembly(typeof(AudioSpectrumFrame).Assembly); Assert.Contains(r.Contracts, x => x.PayloadType == typeof(NowPlayingState)); Assert.Contains(r.Contracts, x => x.PayloadType == typeof(MediaColourPalette)); Assert.Contains(r.Contracts, x => x.PayloadType == typeof(AudioSpectrumFrame)); }
    [Fact] public void CompatibleVersionIsRecognised() { var r = Registry(); Assert.True(r.IsCompatible(new("desktop-shrine.media.now-playing", new(1,0,0)), new("desktop-shrine.media.now-playing", VersionRange.Between(new(1,0,0), new(2,0,0))))); }
    [Fact] public void IncompatibleMajorIsRejected() { var r=Registry(); Assert.False(r.IsCompatible(new("desktop-shrine.media.now-playing",new(1,0,0)),new("desktop-shrine.media.now-playing",VersionRange.Between(new(2,0,0),new(3,0,0))))); }
    private static ContractRegistry Registry(){var r=new ContractRegistry();r.RegisterAssembly(typeof(NowPlayingState).Assembly);return r;}
}
