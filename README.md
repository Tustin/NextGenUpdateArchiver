# NextGenUpdateArchiver
NextGenUpdate scraper for the sake of archival purposes.


# Usage
This tool can export both user profiles and all threads/posts in specified forum(s).

## Setup

1. Install .NET core 3.1 sdk on your system.
2. `cd` into `./NextGenUpdateArchiver/NextGenUpdateArchiver`.
3. Follow one of the options below.



### User export

1. Simply run `dotnet run users`. This will begin dumping all the user accounts created after Feb 1, 2014 (The date of our most up to date database dump).


### Thread export

1. (Optional) Copy one of the json files found in the `/sections` folder into the current folder and rename it to `forums.json`. The program will only grab the threads and posts from these sections. 
2. Run `dotnet run threads`. 

#### For authenticated requests.

You can use a logged in account two ways.
1. Create a `cookies.json` file with this format `['remember_me_xxxyyyzzz', 'remember_me_token_here
2. Create 2 environment variables: `NGU_SESSION_NAME` AND `NGU_SESSION_VALUE`. `NGU_SESSION_NAME` should be the name of the session cookie (looks like `remember_me_xxxyyyzzz`) and the value should be the value of this cookie.
