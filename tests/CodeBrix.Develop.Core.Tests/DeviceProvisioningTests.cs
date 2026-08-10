using System.Linq;
using CodeBrix.Develop.Core.Remote;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class DeviceProvisioningTests
{
    [Fact]
    public void HasVideoAndInputGroups_is_true_when_both_are_present()
        => DeviceProvisioning.HasVideoAndInputGroups("debian adm cdrom sudo video input netdev\n")
            .Should().BeTrue();

    [Theory]
    [InlineData("debian sudo video netdev")]   // input missing
    [InlineData("debian sudo input netdev")]   // video missing
    [InlineData("debian sudo netdev")]          // both missing
    [InlineData("")]
    [InlineData(null)]
    public void HasVideoAndInputGroups_is_false_when_either_is_missing(string listing)
        => DeviceProvisioning.HasVideoAndInputGroups(listing).Should().BeFalse();

    [Fact]
    public void HasVideoAndInputGroups_does_not_match_substrings()
        // "videofoo" / "inputbar" are not the video / input groups
        => DeviceProvisioning.HasVideoAndInputGroups("debian videofoo inputbar").Should().BeFalse();

    [Fact]
    public void IsServiceMasked_is_true_for_masked()
        => DeviceProvisioning.IsServiceMasked("masked\n").Should().BeTrue();

    [Theory]
    [InlineData("enabled")]
    [InlineData("disabled")]
    [InlineData("static")]
    [InlineData("generated")]
    [InlineData("")]
    [InlineData(null)]
    public void IsServiceMasked_is_false_for_anything_else(string state)
        => DeviceProvisioning.IsServiceMasked(state).Should().BeFalse();

    [Fact]
    public void MissingNativeDependencies_finds_the_ones_absent_from_the_loader_cache()
    {
        //Arrange — a cache with fontconfig + icu present, libinput + xkbcommon absent
        var ldconfig =
            "\tlibfontconfig.so.1 (libc6,x86-64) => /lib/x86_64-linux-gnu/libfontconfig.so.1\n" +
            "\tlibicuuc.so.76 (libc6,x86-64) => /lib/x86_64-linux-gnu/libicuuc.so.76\n";

        //Act
        var missing = DeviceProvisioning.MissingNativeDependencies(ldconfig);

        //Assert
        missing.Select(dependency => dependency.AptPackage)
            .Should().BeEquivalentTo(new[] { "libinput10", "libxkbcommon-dev" });
    }

    [Fact]
    public void MissingNativeDependencies_is_empty_when_all_are_present()
    {
        //Arrange
        var ldconfig =
            "libfontconfig.so.1 => x\nlibicuuc.so.76 => x\nlibinput.so.10 => x\nlibxkbcommon.so.0 => x\n";

        //Act / Assert — the versioned libxkbcommon.so.0 satisfies the substring
        DeviceProvisioning.MissingNativeDependencies(ldconfig).Should().BeEmpty();
    }

    [Fact]
    public void MissingNativeDependencies_reports_all_for_a_bare_device()
        => DeviceProvisioning.MissingNativeDependencies("")
            .Should().HaveCount(DeviceProvisioning.FrameBufferNativeDependencies.Count);

    [Fact]
    public void AptInstallCommand_lists_the_packages_non_interactively()
        => DeviceProvisioning.AptInstallCommand(new[] { "libfontconfig1", "libinput10" })
            .Should().Be("sudo apt install -y libfontconfig1 libinput10");

    [Fact]
    public void The_fatal_native_dependencies_are_fontconfig_and_icu()
        => DeviceProvisioning.FrameBufferNativeDependencies
            .Where(dependency => dependency.Fatal)
            .Select(dependency => dependency.AptPackage)
            .Should().BeEquivalentTo(new[] { "libfontconfig1", "libicu-dev" });

    // The real vtconsole listing from the WinBook TW802: a dummy console that
    // never owns the screen, and the framebuffer console that does. The index
    // is not fixed, which is why the console is matched by name.
    const string AttachedConsole = """
        /sys/class/vtconsole/vtcon0 0
        /sys/class/vtconsole/vtcon1 1
        """;

    const string DetachedConsole = """
        /sys/class/vtconsole/vtcon0 0
        /sys/class/vtconsole/vtcon1 0
        """;

    [Fact]
    public void IsFrameBufferConsoleAttached_detects_a_console_still_owning_the_screen()
        => DeviceProvisioning.IsFrameBufferConsoleAttached(AttachedConsole).Should().BeTrue();

    [Fact]
    public void IsFrameBufferConsoleAttached_accepts_a_detached_console()
        => DeviceProvisioning.IsFrameBufferConsoleAttached(DetachedConsole).Should().BeFalse();

    [Fact]
    public void IsFrameBufferConsoleAttached_reports_false_when_no_console_is_listed()
    {
        //Arrange & Act & Assert — a device with no framebuffer console has
        //nothing to detach, so a run must not be blocked on it.
        DeviceProvisioning.IsFrameBufferConsoleAttached("").Should().BeFalse();
        DeviceProvisioning.IsFrameBufferConsoleAttached(null).Should().BeFalse();
        DeviceProvisioning.IsFrameBufferConsoleAttached("garbage").Should().BeFalse();
    }

    [Fact]
    public void IsServiceActive_reads_the_systemctl_is_active_result()
    {
        DeviceProvisioning.IsServiceActive("active").Should().BeTrue();
        DeviceProvisioning.IsServiceActive("active\n").Should().BeTrue();
        DeviceProvisioning.IsServiceActive("inactive").Should().BeFalse();
        DeviceProvisioning.IsServiceActive("").Should().BeFalse();
        DeviceProvisioning.IsServiceActive(null).Should().BeFalse();
    }

    [Fact]
    public void Unmasking_the_login_also_starts_it()
    {
        //Arrange & Act & Assert — masking used --now, which stopped the unit,
        //so unmasking alone would leave the screen blank until a reboot.
        DeviceProvisioning.UnmaskGettyCommand
            .Should().Be("sudo systemctl unmask getty@tty1 && sudo systemctl start getty@tty1");
    }

    [Fact]
    public void The_console_commands_match_the_framebuffer_console_by_name()
    {
        //Arrange & Act & Assert — the vtcon index varies by device, so every
        //console command selects on the name and writes only the bind flag.
        DeviceProvisioning.FrameBufferConsoleStateCommand.Should().Contain("frame buffer");
        DeviceProvisioning.DetachFrameBufferConsoleCommand.Should().Contain("echo 0 > \"$d/bind\"");
        DeviceProvisioning.AttachFrameBufferConsoleCommand.Should().Contain("echo 1 > \"$d/bind\"");
        //Nothing on disk changes: a reboot alone restores the console.
        DeviceProvisioning.DetachFrameBufferConsoleCommand.Should().NotContain("grub");
    }

    // The real /proc/mounts root line from the WinBook TW802 in the state that
    // failed a deploy: root mounted ro after systemd-remount-fs died.
    const string ReadOnlyRootMounts = """
        proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0
        /dev/mmcblk1p2 / ext4 ro,relatime 0 0
        /dev/mmcblk1p1 /boot/efi vfat rw,relatime,fmask=0077 0 0
        """;

    const string WritableRootMounts = """
        proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0
        /dev/mmcblk1p2 / ext4 rw,relatime 0 0
        /dev/mmcblk1p1 /boot/efi vfat rw,relatime,fmask=0077 0 0
        """;

    [Fact]
    public void IsRootFilesystemReadOnly_detects_a_read_only_root()
        => DeviceProvisioning.IsRootFilesystemReadOnly(ReadOnlyRootMounts).Should().BeTrue();

    [Fact]
    public void IsRootFilesystemReadOnly_accepts_a_writable_root()
        => DeviceProvisioning.IsRootFilesystemReadOnly(WritableRootMounts).Should().BeFalse();

    [Fact]
    public void IsRootFilesystemReadOnly_ignores_read_only_mounts_that_are_not_the_root()
    {
        //Arrange — a writable root beside read-only mounts of its own; only
        //the root mount decides whether the deploy can write.
        var mounts = """
            /dev/mmcblk1p2 / ext4 rw,relatime 0 0
            /dev/sr0 /media/cdrom iso9660 ro,relatime 0 0
            /dev/loop0 /snap/core squashfs ro,nodev,relatime 0 0
            """;

        //Act & Assert
        DeviceProvisioning.IsRootFilesystemReadOnly(mounts).Should().BeFalse();
    }

    [Fact]
    public void IsRootFilesystemReadOnly_does_not_mistake_other_options_for_ro()
    {
        //Arrange — "relatime" and "errors=remount-ro" both contain "ro" as a
        //substring; only a standalone "ro" option means read-only.
        var mounts = "/dev/mmcblk1p2 / ext4 rw,relatime,errors=remount-ro 0 0";

        //Act & Assert
        DeviceProvisioning.IsRootFilesystemReadOnly(mounts).Should().BeFalse();
    }

    [Fact]
    public void IsRootFilesystemReadOnly_reports_false_when_the_listing_is_unusable()
    {
        //Arrange & Act & Assert — an unreadable or root-less listing must not
        //block a run that would otherwise have worked.
        DeviceProvisioning.IsRootFilesystemReadOnly("").Should().BeFalse();
        DeviceProvisioning.IsRootFilesystemReadOnly(null).Should().BeFalse();
        DeviceProvisioning.IsRootFilesystemReadOnly("proc /proc proc rw 0 0").Should().BeFalse();
        DeviceProvisioning.IsRootFilesystemReadOnly("garbage").Should().BeFalse();
    }

    [Fact]
    public void The_remount_command_makes_the_root_filesystem_writable()
    {
        DeviceProvisioning.RemountRootReadWriteCommand.Should().Be("sudo mount -o remount,rw /");
        DeviceProvisioning.MountedFilesystemsCommand.Should().Be("cat /proc/mounts");
    }

    [Fact]
    public void Command_builders_use_the_given_user_and_the_console_tty()
    {
        DeviceProvisioning.AddVideoInputGroupsCommand("debian")
            .Should().Be("sudo usermod -aG video,input debian");
        DeviceProvisioning.PersistentGroupsCommand("debian").Should().Be("id -nG debian");
        DeviceProvisioning.GettyEnabledCommand.Should().Be("systemctl is-enabled getty@tty1");
        DeviceProvisioning.GettyActiveCommand.Should().Be("systemctl is-active getty@tty1");
    }
}
