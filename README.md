# mysql-runner
A brute-force mysql file runner, eg for (mostly) restoring (potentially) wonky backups or
resuming backups from existing sources without IGNOREs

## usage
Run with `--help` to get help any time and forget about this doc (:

```
Usage: mysql-runner {options} <file.sql> {<file.sql>...}
  options:
  -d            Database name (required)
  -h            MySql host (default: localhost)
  -u            MySql user (required)
  -p            MySql password (required)
  -P            port (default 3306)
  -q            operate quietly
  -s            stop on errors (defaults is to report and continue)
  --help        this help
```

## why?
I needed to be able to restore scripts made by `mysqldump` and had to overcome:
- `mysql` would crash on long lines
- I wanted to be able to 'resume' so needed to be able to ignore errors
- I tried HeidiSQL and it predicted 22 hours to run in scripts that took this tool about 40 minutes
- I wanted something that would work cross-platform

## whatevs
Ok, sure. I've wanted this often enough to make it. I'll use it. If it doesn't matter to you,
that's perfectly fine -- there's plenty of interesting stuff out there (:
