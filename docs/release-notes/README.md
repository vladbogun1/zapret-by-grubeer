# Release notes

One file per version, named after the version without a leading `v`:

```text
docs/release-notes/1.1.0.md
```

The release workflow picks up `docs/release-notes/<version>.md` when the tag `v<version>` is pushed, and
appends the build commit and the installer's SHA-256 to it. Keeping the notes in the repository means they
are reviewable in a pull request before they become the public text of a release, instead of being typed
into the GitHub UI at publish time.

If the file is missing the workflow still publishes, with a minimal body and a warning in the run log — a
missing changelog should not block a release, but it should be visible.

Write for the person deciding whether to install the update: what changed for them, what to expect, and
anything they must do. Technical detail belongs in the commit history.
