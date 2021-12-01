const
    os = require("os"),
    path = require("path"),
    {
        mkdir,
        copyFile,
        CopyFileOptions,
        resolveHomePath,
        ls,
        LsOptions,
        FsEntities
    } = require("yafs"),
    runSequence = requireModule("run-sequence"),
    gulp = requireModule("gulp");

gulp.task("publish-for-platform", (done) => {
    process.env.DOTNET_PUBLISH_RUNTIMES = determinePublishTarget();
    runSequence("dotnet-publish", done);
});

gulp.task("publish-local", ["publish-for-platform"], async () => {
    const
        artifact = await findPublishedArtifact(),
        target = resolveHomePath(".local/bin");
    await mkdir(target);
    await copyFile(artifact, target, CopyFileOptions.overwriteExisting);
});

const publishTargets = {
    "linux": "linux-x64",
    "win32": "win10-x64",
    "darwin": "osx-64"
};

function determinePublishTarget() {
    const
        platform = os.platform(),
        publishTarget = publishTargets[platform];
    if (!publishTarget) {
        throw new Error(`Unable to determine publish target for platform ${platform}. Targeted platforms are: \n${Object.keys(publishTargets).join(", ")}`);
    }
    return publishTarget;
}

async function findPublishedArtifact() {
    const
        publishTarget = determinePublishTarget(),
        artifacts = await ls("src", {
            entities: FsEntities.files,
            fullPaths: true,
            match: new RegExp(`mysql-runner[\\\\/]bin(\\\\|/)*.*${publishTarget}.*publish.*mysql-runner.exe$`),
            recurse: true
        });
    if (artifacts.length === 1) {
        return artifacts[0];
    }
    throw new Error(`Can't find single published artifact for target platform ${publishTarget}`);
}
