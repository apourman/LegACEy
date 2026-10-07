# Server provisioning

`provision.yml` turns a fresh Ubuntu server into a LegACEy host: an admin account,
SSH hardening, firewall, MySQL with the ACE databases, the retail DATs, `Config.js`,
the `legacey-ace` systemd unit, daily MySQL backups, and the restricted `ace-deploy`
account that CI deploys through. On a fresh server it finishes by deploying the
latest GitHub release with the same helper CI uses; after that, every merge to
`master` deploys itself.

## Fresh server

You need, on this machine: `ansible`, `gh` (logged in), your SSH key loaded in
`ssh-agent`, the CI deploy key's public half at `~/.ssh/legacey-cd_ed25519.pub`, and the
retail DATs at `~/ace_dats/retail/`. Change any default in `group_vars/all/local.yml`
(untracked; see `group_vars/all/defaults.yml`).

```bash
cd scripts/deploy/ansible
ansible-galaxy collection install -r requirements.yml
cp inventory.example.yml inventory.yml   # set the server address and the provider's login user
ansible-playbook provision.yml
```

The playbook generates the MySQL password into `secrets/` (untracked) and updates
the `ACE_DEPLOY_KNOWN_HOSTS` GitHub secret with the server's new host key. Rerunning
it is safe; it never touches existing databases or redeploys ACE.

## Useful commands on the server

```bash
sudo systemctl status legacey-ace
sudo journalctl -u legacey-ace -f
sudo /usr/local/sbin/legacey-backup-mysql   # dumps go to /var/backups/legacey/mysql
```

To restore old data, stop ACE, `gunzip < dump.sql.gz | sudo mysql`, rerun
`provision.yml` (a full dump brings back the old MySQL users, so this resets the ACE
password to match `Config.js`), and start ACE.
