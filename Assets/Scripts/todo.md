rework shoot system to be more inline with the game
- spawn shots that shoot directly down with bigger splash radius

bubble special spawns on player root not model

remove auto lobby
switch to host/join model

we swim faster on enemy ink than ground (should be slower)

## gameplay logic
all players to spawn for game start, lock players, reset map
3,2,1
players play for 2 minutes with everything normal
now or never starts
coverage bar fades out (WHOS WINNING?????)
1 more minute
TIMES UP
all players locked
all players see overhead cam
big coverage bar at the bottom
bar fills on both sides to 20% coverage or 80% coverage of team that lost (whichever is greater)
pause for a moment
quick fill to true values (0.2s, remaining nutral is ignored from the bar but shown via the percentage values)
WINNERS ARE SHOWN (YAY GOOD JOB)
hold for 10s or so, return to game, unlock players (free roam until next match)
