There's a reassuring quality to a folder of text files. You can open them in almost anything. Copy them to a drive. Put them in version control. Read them without asking a service for permission.

That's where the words on this site live.

## A small publishing routine

Each post has two files: a Markdown document for the words, and a JSON file for the title, date, description, and topics.

```text
content/posts/
  hello-world.json
  hello-world.md
```

Write the words in whatever editor feels comfortable. Check the metadata. Restart the blog when you're ready to publish.

## Less to look after

There's no database to migrate and no browser editor to keep secure. Backups are copies of the content folder and configuration. The application renders each Markdown document once at startup, then serves the finished article.

This suits a quiet personal blog. If the site needs something more one day, the files are still just files.

That's a good place to start.
