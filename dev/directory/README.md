# Development directory

A real Active Directory domain controller (Samba AD DC: free, open source) for trying directory sign-in locally.
It is **not** part of the application: it only exists so the LDAP code can be exercised against the real schema
(`sAMAccountName`, `userPrincipalName`, `memberOf`, `userAccountControl`, nested groups) instead of a mock.

```bash
./up.sh                                   # start + seed, safe to repeat
docker compose down                       # stop (data is kept in the stockline-directory volume)
docker compose down -v                    # stop and forget everything
```

| Port (host) | Protocol |
|---|---|
| `1636` | LDAPS (use this one: the password travels in the clear over plain LDAP) |
| `1389` | LDAP |

Domain `stockline.test`. The seed creates the groups `stockline-admins`, `stockline-buyers`, `stockline-suppliers`,
`stockline-production` and `purchasing-team` (a member of `stockline-buyers`, to exercise nested groups), the read-only
service account `svc-stockline`, and the people listed in the API README. Passwords are for development only.

Changing groups in the container takes effect at the person's next sign-in:

```bash
docker exec stockline-directory samba-tool group addmembers stockline-admins ben.buyer
docker exec stockline-directory samba-tool user disable ben.buyer
```

The container needs the `SYS_ADMIN` capability only while provisioning, because Samba stores the NT ACLs of its
SYSVOL share in extended attributes.
