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
- I've written something like this in the past before and lost it
- I wrote almost exactly this the day I created this repo and did `git reset --hard` and lost it :/
  - but then I made a new one (:

## situation [X] breaks!
Let me know about it -- raise an issue. The primary aim was to get a dump file running in without
interruption. So I _know_ that there are some naive assumptions, in particular that `/* ... */` is a
"multi-line comment which never actually spans multiple lines, but leaves a no-op query behind", eg:
```
/* some mysqldump options stuff goes here */;
```
so if you have legitimate multi-line comments or something useful after the comment, this tool won't
(currently) behave as expected. But it could, if you give me an example (:
