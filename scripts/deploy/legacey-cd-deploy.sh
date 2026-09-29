#!/usr/bin/env bash
set -euo pipefail

readonly install_root=/opt/legacey
readonly current_link="$install_root/ace"
readonly releases_dir="$install_root/releases"
readonly deploy_user=ace-deploy
readonly service=legacey-ace.service
readonly backup=/usr/local/sbin/legacey-backup-mysql

usage() { echo "Usage: $0 <40-character-git-sha>" >&2; exit 2; }
[[ $# -eq 1 ]] || usage
release_sha=$1
[[ "$release_sha" =~ ^[0-9a-f]{40}$ ]] || usage

release="$releases_dir/$release_sha"
[[ -d "$release" && ! -L "$release" ]] || { echo "Release does not exist as a directory: $release" >&2; exit 1; }
[[ -r "$release/ACE.Server.dll" ]] || { echo "Release is missing ACE.Server.dll" >&2; exit 1; }

# Only root may replace the parent of the shared configuration or active link.
[[ -d "$install_root" && ! -L "$install_root" && $(stat -c %u "$install_root") == 0 \
  && -z $(find "$install_root" -maxdepth 0 -perm /022 -print) ]] || {
  echo "Installation root must be root-owned and not writable by other users" >&2; exit 1;
}
[[ ! -L "$install_root/shared" ]] || { echo "Shared directory must not be a symlink" >&2; exit 1; }

shared_dir="$install_root/shared"
install -d -o root -g ace -m 0750 "$shared_dir"

copy_from_ace() {
  local source=$1 target=$2 owner=$3 temporary
  temporary=$(mktemp "$shared_dir/.copy.XXXXXX")
  if ! runuser -u ace -- cat -- "$source" > "$temporary"; then
    rm -f -- "$temporary"
    return 1
  fi
  chown "$owner:ace" "$temporary"
  chmod 0640 "$temporary"
  mv -Tf -- "$temporary" "$target"
}

[[ ! -L "$shared_dir/Config.js" ]] || { echo "Shared Config.js must not be a symlink" >&2; exit 1; }
if [[ ! -e "$shared_dir/Config.js" && -f "$current_link/Config.js" ]]; then
  copy_from_ace "$current_link/Config.js" "$shared_dir/Config.js" root
fi
[[ -f "$shared_dir/Config.js" && -r "$shared_dir/Config.js" ]] || { echo "Shared Config.js is missing" >&2; exit 1; }
chown root:ace "$shared_dir/Config.js"
chmod 0640 "$shared_dir/Config.js"
runuser -u "$deploy_user" -- ln -sfnT "$shared_dir/Config.js" "$release/Config.js"

[[ ! -L "$shared_dir/log4net.config" ]] || { echo "Shared log4net.config must not be a symlink" >&2; exit 1; }
if [[ -f "$current_link/log4net.config" && ! -e "$shared_dir/log4net.config" ]]; then
  copy_from_ace "$current_link/log4net.config" "$shared_dir/log4net.config" root
fi
if [[ -f "$shared_dir/log4net.config" && -r "$shared_dir/log4net.config" ]]; then
  chown root:ace "$shared_dir/log4net.config"
  chmod 0640 "$shared_dir/log4net.config"
  runuser -u "$deploy_user" -- ln -sfnT "$shared_dir/log4net.config" "$release/log4net.config"
fi

previous_target=''
if [[ -L "$current_link" ]]; then
  previous_target=$(readlink -f "$current_link")
elif [[ -d "$current_link" ]]; then
  bootstrap="$releases_dir/bootstrap"
  [[ ! -e "$bootstrap" && ! -L "$bootstrap" ]] || { echo "Bootstrap release already exists" >&2; exit 1; }
  mv -T "$current_link" "$bootstrap"
  ln -s "$bootstrap" "$current_link"
  previous_target="$bootstrap"
fi

for db_type in Authentication Shard World; do
  updates_dir="$release/DatabaseSetupScripts/Updates/$db_type"
  runuser -u "$deploy_user" -- mkdir -p "$updates_dir"
  state_file="$shared_dir/${db_type}_applied_updates.txt"
  source_file="$updates_dir/applied_updates.txt"
  old_state="$current_link/DatabaseSetupScripts/Updates/$db_type/applied_updates.txt"
  [[ ! -L "$state_file" ]] || { echo "Shared update state must not be a symlink: $state_file" >&2; exit 1; }
  if [[ ! -e "$state_file" ]]; then
    if [[ -f "$old_state" ]]; then
      copy_from_ace "$old_state" "$state_file" ace
    else
      install -o ace -g ace -m 0640 /dev/null "$state_file"
    fi
  fi
  [[ -f "$state_file" ]] || { echo "Shared update state must be a file: $state_file" >&2; exit 1; }
  chown ace:ace "$state_file"
  chmod 0640 "$state_file"
  runuser -u "$deploy_user" -- ln -sfnT "$state_file" "$source_file"
done

"$backup"

atomic_switch() {
  local target=$1
  local temporary="$install_root/.ace.current.$$"
  ln -s "$target" "$temporary"
  mv -Tf "$temporary" "$current_link"
}

atomic_switch "$release"

healthy=false
if systemctl restart "$service"; then
  for _ in {1..90}; do
    if systemctl is-active --quiet "$service" \
      && ss -H -lun | awk '$5 ~ /:9000$/ { found=1 } END { exit !found }' \
      && journalctl -u "$service" --since "-2 minutes" --no-pager | grep -q 'World is now open'; then
      healthy=true
      break
    fi
    sleep 1
  done
fi

if [[ "$healthy" != true ]]; then
  echo "ACE health check failed for $release_sha" >&2
  if [[ -n "$previous_target" && -d "$previous_target" ]]; then
    echo "Rolling back the application symlink only; database changes are retained." >&2
    atomic_switch "$previous_target"
    systemctl restart "$service" || true
  fi
  exit 1
fi

find "$releases_dir" -mindepth 1 -maxdepth 1 -type d \
  -regextype posix-extended -regex '.*/[0-9a-f]{40}' \
  -printf '%T@ %p\0' | sort -zr | tail -z -n +5 | cut -z -d ' ' -f 2- | \
  xargs -0 -r runuser -u "$deploy_user" -- rm -rf --
