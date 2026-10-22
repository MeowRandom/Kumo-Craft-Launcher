import java.io.File;

public final class OptiFineInstallerBridge {
    private OptiFineInstallerBridge() {
    }

    public static void main(String[] args) throws Exception {
        if (args.length != 1) {
            throw new IllegalArgumentException("Expected the Minecraft data directory.");
        }
        optifine.Installer.doInstall(new File(args[0]));
    }
}
