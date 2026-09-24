{
  description = "Dev tooling and CI checks for the Rust dedicated server scripts and Carbon mods";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    git-hooks = {
      url = "github:cachix/git-hooks.nix";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs =
    {
      self,
      nixpkgs,
      git-hooks,
    }:
    let
      system = "x86_64-linux";
      pkgs = import nixpkgs { inherit system; };
      lib = pkgs.lib;

      # Secrets and world state that must never be committed, as listed in CLAUDE.md.
      # pre-commit matches `files` with re.search against repo-relative paths.
      guardedPathPatterns = [
        "(^|/)\\.rcon-password$"
        "(^|/)config\\.webpanel\\.json$"
        "(^|/)relay_cfg\\.json$"
        "(^|/)users\\.cfg$"
        "(^|/)serverauto\\.cfg$"
        "(^|/)oxide\\.[^/]*\\.data$"
        "(^|/)[^/]*\\.sav(\\.[0-9]+)?$"
        "(^|/)[^/]*\\.db$"
        "(^|/)[^/]*\\.db-wal$"
        "(^|/)[^/]*\\.db-shm$"
        "(^|/)server\\.log[^/]*$"
        "^home/"
        "^steamcmd/"
        "^backups/"
      ];

      pre-commit = git-hooks.lib.${system}.run {
        # The git-fetched source holds tracked files only, never the untracked install or its secrets.
        src = self;
        hooks = {
          shellcheck.enable = true;
          actionlint.enable = true;
          check-json.enable = true;
          nixfmt.enable = true;

          gitleaks = {
            enable = true;
            name = "gitleaks";
            description = "Detect hardcoded secrets in staged content.";
            entry = "${pkgs.gitleaks}/bin/gitleaks git --no-banner --redact --ignore-gitleaks-allow --staged .";
            pass_filenames = false;
            always_run = true;
          };

          forbidden-paths = {
            enable = true;
            name = "forbidden-paths";
            description = "Refuse to commit secrets or generated Carbon/world state.";
            language = "fail";
            entry = "forbidden-paths: secret or generated Carbon/world state staged; see CLAUDE.md";
            files = lib.concatStringsSep "|" guardedPathPatterns;
          };
        };
      };
    in
    {
      devShells.${system}.default = pkgs.mkShell {
        packages = [
          pkgs.shellcheck
          pkgs.actionlint
          pkgs.jq
          pkgs.gitleaks
          pkgs.websocat
          pkgs.nixfmt
        ];
        shellHook = pre-commit.shellHook;
      };

      checks.${system}.pre-commit = pre-commit;

      formatter.${system} = pkgs.nixfmt;
    };
}
